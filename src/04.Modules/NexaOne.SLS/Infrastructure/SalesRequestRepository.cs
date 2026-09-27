using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SLS.Application;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.SLS.Infrastructure;

/// <summary>판매요청과 수주 초안의 저장 및 원자적 연결을 소유합니다.</summary>
internal sealed class SalesRequestRepository : ISalesRequestStore
{
    private readonly ServiceObjectProcessor _processor;
    private readonly IBusinessMasterDirectory _masters;

    public SalesRequestRepository(EesDataSource dataSource, IBusinessMasterDirectory masters)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _masters = masters ?? throw new ArgumentNullException(nameof(masters));
    }

    public async Task<SalesRequestDraftInsertOutcome> TryCreateDraftAsync(
        SalesRequestDraft draft, CancellationToken ct)
    {
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var customer = await _masters.FindCustomerAsync(transaction, draft.CustomerId, ct);
                var product = await _masters.FindProductAsync(transaction, draft.ProductId, ct);
                if (customer?.IsActive != true || product?.ValidState != "Valid")
                    return SalesRequestDraftInsertOutcome.InvalidReference;

                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO SLS_SALES_REQUEST
                        (SALES_REQUEST_ID, SALES_REQUEST_NAME, CUSTOMER_ID, PRODUCT_ID,
                         REQUEST_DATE, REQUEST_QTY, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES (@SalesRequestId, @SalesRequestName, @CustomerId, @ProductId,
                            @RequestDate, @RequestQty, 'Draft', @ActorId, @now, @ActorId, @now)
                    """,
                    new
                    {
                        draft.SalesRequestId, draft.SalesRequestName,
                        customer.CustomerId, product.ProductId,
                        draft.RequestDate, draft.RequestQty, draft.ActorId, now = DateTime.UtcNow,
                    }, transaction, cancellationToken: ct));
                if (affected != 1)
                    throw new DBConcurrencyException($"Sales request insert affected {affected} rows.");
                return SalesRequestDraftInsertOutcome.Created;
            }, IsolationLevel.Serializable, ct);
        }
        catch (DbException error) when (IsIdentityConflict(error, "SLS_SALES_REQUEST", "SALES_REQUEST_ID"))
        {
            return SalesRequestDraftInsertOutcome.IdentityConflict;
        }
    }

    public async Task<SalesRequestReceiptOutcome> TryReceiveAsync(
        SalesRequestReceipt receipt, CancellationToken ct)
    {
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var parameters = new
                {
                    receipt.SalesRequestId, receipt.SalesOrderId,
                    receipt.SalesOrderName, receipt.PlanStartDate, receipt.PlanEndDate,
                    receipt.ActorId, now = DateTime.UtcNow,
                };
                var request = await connection.QuerySingleOrDefaultAsync<RequestRow>(new CommandDefinition(
                    """
                    SELECT STATUS AS Status, SALES_ORDER_ID AS SalesOrderId,
                           CUSTOMER_ID AS CustomerId, PRODUCT_ID AS ProductId,
                           REQUEST_QTY AS RequestQty
                    FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID = @SalesRequestId
                    """, parameters, transaction, cancellationToken: ct));
                if (request is null) return SalesRequestReceiptOutcome.RequestNotFound;
                if (request.Status != "Draft" || request.SalesOrderId is not null || request.RequestQty <= 0)
                    return SalesRequestReceiptOutcome.NotReceivable;
                if (string.IsNullOrWhiteSpace(request.CustomerId) || string.IsNullOrWhiteSpace(request.ProductId))
                    return SalesRequestReceiptOutcome.InvalidReference;

                var customer = await _masters.FindCustomerAsync(transaction, request.CustomerId, ct);
                var product = await _masters.FindProductAsync(transaction, request.ProductId, ct);
                if (customer?.IsActive != true || product?.ValidState != "Valid")
                    return SalesRequestReceiptOutcome.InvalidReference;
                var plantId = await _masters.FindPlantAsync(transaction, receipt.PlantId, ct);
                if (plantId is null) return SalesRequestReceiptOutcome.PlantNotFound;

                // 상태 CAS는 요청 행에만 작용한다. MDM 적격성은 같은 Serializable 트랜잭션의 owner directory가 검증한다.
                var updated = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE SLS_SALES_REQUEST
                       SET STATUS = 'Confirmed', SALES_ORDER_ID = @SalesOrderId,
                           UPDATED_BY = @ActorId, UPDATED_AT = @now
                     WHERE SALES_REQUEST_ID = @SalesRequestId AND STATUS = 'Draft'
                       AND SALES_ORDER_ID IS NULL AND REQUEST_QTY > 0
                    """, parameters, transaction, cancellationToken: ct));
                if (updated == 0) return SalesRequestReceiptOutcome.NotReceivable;
                if (updated != 1) throw new DBConcurrencyException($"Sales request update affected {updated} rows.");

                var inserted = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO SLS_SALES_ORDER
                        (SALES_ORDER_ID, PLANT_ID, SALES_ORDER_NAME, CUSTOMER_ID, PRODUCT_ID,
                         PLAN_START_DATE, PLAN_END_DATE, PLAN_QTY, OWNER_ID, STATUS,
                         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES (@SalesOrderId, @PlantId, @SalesOrderName, @CustomerId, @ProductId,
                            @PlanStartDate, @PlanEndDate, @RequestQty, @ActorId, 'Draft',
                            @ActorId, @now, @ActorId, @now)
                    """, new
                    {
                        receipt.SalesOrderId, PlantId = plantId, receipt.SalesOrderName,
                        customer.CustomerId, product.ProductId, request.RequestQty,
                        receipt.PlanStartDate, receipt.PlanEndDate, receipt.ActorId,
                        parameters.now,
                    }, transaction, cancellationToken: ct));
                if (inserted != 1) throw new DBConcurrencyException($"Sales order insert affected {inserted} rows.");
                return SalesRequestReceiptOutcome.Received;
            }, IsolationLevel.Serializable, ct);
        }
        catch (DbException error) when (IsIdentityConflict(error, "SLS_SALES_ORDER", "SALES_ORDER_ID"))
        {
            return SalesRequestReceiptOutcome.OrderIdentityConflict;
        }
    }

    public Task<SalesRequestWithdrawOutcome> TryWithdrawAsync(
        string salesRequestId, string actorId, CancellationToken ct)
        => _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE SLS_SALES_REQUEST
                   SET STATUS = 'Cancelled', UPDATED_BY = @actorId, UPDATED_AT = @now
                 WHERE SALES_REQUEST_ID = @salesRequestId AND STATUS = 'Draft' AND SALES_ORDER_ID IS NULL
                """, new { salesRequestId, actorId, now = DateTime.UtcNow }, transaction, cancellationToken: ct));
            if (updated == 1) return SalesRequestWithdrawOutcome.Withdrawn;
            if (updated != 0) throw new DBConcurrencyException($"Sales request withdraw affected {updated} rows.");

            var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID = @salesRequestId",
                new { salesRequestId }, transaction, cancellationToken: ct));
            return exists.HasValue
                ? SalesRequestWithdrawOutcome.NotWithdrawable
                : SalesRequestWithdrawOutcome.RequestNotFound;
        }, IsolationLevel.Serializable, ct);

    private static bool IsIdentityConflict(DbException error, string table, string column)
    {
        var uniqueViolation = error switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
                => error.GetType().GetProperty("Number")?.GetValue(error) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        return uniqueViolation
               && (error.Message.Contains($"PK_{table}", StringComparison.OrdinalIgnoreCase)
                   || error.Message.Contains($"{table}.{column}", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RequestRow
    {
        public string Status { get; set; } = string.Empty;
        public string? SalesOrderId { get; set; }
        public string? CustomerId { get; set; }
        public string? ProductId { get; set; }
        public decimal RequestQty { get; set; }
    }
}
