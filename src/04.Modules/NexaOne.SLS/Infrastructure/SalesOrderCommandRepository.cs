using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.SLS.Application;

namespace NexaOne.SLS.Infrastructure;

/// <summary>수주 초안과 단순 전이의 상태 조건을 같은 직렬화 트랜잭션에서 집행합니다.</summary>
internal sealed class SalesOrderCommandRepository : ISalesOrderCommandStore
{
    private readonly ServiceObjectProcessor _processor;
    private readonly IBusinessMasterDirectory _masters;

    public SalesOrderCommandRepository(EesDataSource dataSource, IBusinessMasterDirectory masters)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _masters = masters ?? throw new ArgumentNullException(nameof(masters));
    }

    public async Task<SalesOrderDraftOutcome> TrySaveDraftAsync(SalesOrderDraft draft, CancellationToken ct)
    {
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var current = await connection.QuerySingleOrDefaultAsync<OrderRow>(new CommandDefinition(
                    "SELECT STATUS AS Status, IS_HOLD AS IsHold FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@SalesOrderId",
                    new { draft.SalesOrderId }, transaction, cancellationToken: ct));
                if (current is not null && (current.Status != "Draft" || current.IsHold != "N"))
                    return SalesOrderDraftOutcome.NotEditable;

                var plantId = await _masters.FindPlantAsync(transaction, draft.PlantId, ct);
                var customer = await _masters.FindCustomerAsync(transaction, draft.CustomerId, ct);
                var product = await _masters.FindProductAsync(transaction, draft.ProductId, ct);
                if (plantId is null || customer?.IsActive != true || product?.ValidState != "Valid")
                    return SalesOrderDraftOutcome.InvalidReference;

                var values = new
                {
                    draft.SalesOrderId, draft.SalesOrderName,
                    PlantId = plantId, CustomerId = customer.CustomerId,
                    ProductId = product.ProductId, draft.PlanStartDate, draft.PlanEndDate,
                    draft.PlanQty, draft.ActorId, Now = DateTime.UtcNow,
                };
                var sql = current is null ? """
                    INSERT INTO SLS_SALES_ORDER
                        (SALES_ORDER_ID, PLANT_ID, SALES_ORDER_NAME, CUSTOMER_ID, PRODUCT_ID,
                         PLAN_START_DATE, PLAN_END_DATE, PLAN_QTY, OWNER_ID, STATUS,
                         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES (@SalesOrderId, @PlantId, @SalesOrderName, @CustomerId, @ProductId,
                            @PlanStartDate, @PlanEndDate, @PlanQty, @ActorId, 'Draft',
                            @ActorId, @Now, @ActorId, @Now)
                    """ : """
                    UPDATE SLS_SALES_ORDER
                       SET PLANT_ID=@PlantId, SALES_ORDER_NAME=@SalesOrderName,
                           CUSTOMER_ID=@CustomerId, PRODUCT_ID=@ProductId,
                           PLAN_START_DATE=@PlanStartDate, PLAN_END_DATE=@PlanEndDate,
                           PLAN_QTY=@PlanQty, OWNER_ID=@ActorId,
                           UPDATED_BY=@ActorId, UPDATED_AT=@Now
                     WHERE SALES_ORDER_ID=@SalesOrderId AND STATUS='Draft' AND IS_HOLD='N'
                    """;
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    sql, values, transaction, cancellationToken: ct));
                if (affected == 0) return SalesOrderDraftOutcome.NotEditable;
                if (affected != 1)
                    throw new DBConcurrencyException($"Sales order draft write affected {affected} rows.");
                return SalesOrderDraftOutcome.Saved;
            }, IsolationLevel.Serializable, ct);
        }
        catch (DbException error) when (IsUniqueViolation(error))
        {
            return SalesOrderDraftOutcome.IdentityConflict;
        }
    }

    public Task<SalesOrderChangeOutcome> TryDeleteDraftAsync(
        string salesOrderId, string actorId, CancellationToken ct)
        => ExecuteGuardedAsync("""
            DELETE FROM SLS_SALES_ORDER
             WHERE SALES_ORDER_ID=@salesOrderId AND STATUS='Draft' AND IS_HOLD='N'
               AND NOT EXISTS (
                   SELECT 1 FROM SLS_SALES_REQUEST WHERE SALES_ORDER_ID=@salesOrderId
               )
            """, salesOrderId, actorId, ct);

    public Task<SalesOrderChangeOutcome> TryConfirmAsync(
        string salesOrderId, string actorId, CancellationToken ct)
        => ExecuteGuardedAsync("""
            UPDATE SLS_SALES_ORDER
               SET STATUS='Confirmed', UPDATED_BY=@actorId, UPDATED_AT=@now
             WHERE SALES_ORDER_ID=@salesOrderId AND STATUS='Draft' AND IS_HOLD='N'
            """, salesOrderId, actorId, ct);

    public Task<SalesOrderChangeOutcome> TryCloseAsync(
        string salesOrderId, string actorId, CancellationToken ct)
        => ExecuteGuardedAsync("""
            UPDATE SLS_SALES_ORDER
               SET STATUS='Closed', UPDATED_BY=@actorId, UPDATED_AT=@now
             WHERE SALES_ORDER_ID=@salesOrderId AND STATUS IN ('Producing', 'Delivered') AND IS_HOLD='N'
            """, salesOrderId, actorId, ct);

    private Task<SalesOrderChangeOutcome> ExecuteGuardedAsync(
        string sql, string salesOrderId, string actorId, CancellationToken ct)
        => _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                sql, new { salesOrderId, actorId, now = DateTime.UtcNow },
                transaction, cancellationToken: ct));
            if (affected == 1) return SalesOrderChangeOutcome.Applied;
            if (affected != 0)
                throw new DBConcurrencyException($"Sales order command affected {affected} rows.");

            var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@salesOrderId",
                new { salesOrderId }, transaction, cancellationToken: ct));
            return exists.HasValue
                ? SalesOrderChangeOutcome.NotAllowed
                : SalesOrderChangeOutcome.NotFound;
        }, IsolationLevel.Serializable, ct);

    private static bool IsUniqueViolation(DbException error) => error switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
            => error.GetType().GetProperty("Number")?.GetValue(error) is int number
               && number is 2601 or 2627,
        _ => false,
    };

    private sealed class OrderRow
    {
        public string Status { get; set; } = string.Empty;
        public string IsHold { get; set; } = string.Empty;
    }
}
