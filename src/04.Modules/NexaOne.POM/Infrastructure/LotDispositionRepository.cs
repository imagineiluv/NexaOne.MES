using System.Data.Common;
using System.Data;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.POM.Infrastructure;

public sealed class LotDispositionRepository : QueryRepository, ILotDispositionRepository
{
    private readonly ITransactionManager _transactionManager;
    private readonly DatabaseEndpoint _endpoint;

    public LotDispositionRepository(EesDataSource dataSource) : base(dataSource)
    {
        _transactionManager = dataSource.Provider.TransactionManager;
        _endpoint = new DatabaseEndpoint("NexaOneEES", dataSource.Provider.Kind, dataSource.ConnectionString);
    }

    public async Task<LotDispositionRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<DispositionRow>(
            SelectSql + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<LotDispositionScope?> GetScopeAsync(
        string plantId,
        string lotId,
        string? workOrderId,
        string? processId,
        string? defectExecutionId,
        string? defectCode,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ScopeRow>(ScopeSql, new
        {
            plantId, lotId, workOrderId, processId, defectExecutionId, defectCode,
        }, ct);
        return row?.ToScope();
    }

    public async Task<bool> TryAddAsync(LotDispositionRecord r, CancellationToken ct = default)
    {
        try
        {
            return await _transactionManager.ExecuteInTransactionAsync(
                _endpoint,
                async (connection, transaction) =>
                {
                    var locked = await connection.ExecuteAsync(new CommandDefinition(
                        GuardLotSql, r, transaction, cancellationToken: ct)).ConfigureAwait(false);
                    if (locked != 1) return false;

                    // Re-read every balance inside the same transaction after taking the LOT write
                    // lock. This avoids a pre-check/write gap and works for both SQL Server and SQLite.
                    var scopeParameters = new
                    {
                        plantId = r.PlantId,
                        lotId = r.LotId,
                        workOrderId = r.WorkOrderId,
                        processId = r.ProcessId,
                        defectExecutionId = r.DefectExecutionId,
                        defectCode = r.DefectCode,
                    };
                    var scope = await connection.QueryFirstOrDefaultAsync<ScopeRow>(new CommandDefinition(
                        ScopeSql, scopeParameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
                    if (scope is null || r.Quantity > scope.ToScope().AvailableQuantity)
                        throw new AllocationConflictException();

                    var inserted = await connection.ExecuteAsync(new CommandDefinition(
                        InsertSql, r, transaction, cancellationToken: ct)).ConfigureAwait(false);
                    if (inserted != 1)
                        throw new DBConcurrencyException(
                            $"Lot disposition insert affected {inserted} rows; expected exactly one.");
                    return true;
                },
                IsolationLevel.Serializable,
                ct).ConfigureAwait(false);
        }
        catch (AllocationConflictException)
        {
            // Throwing from the callback deliberately rolls back the LOT touch before projecting a
            // normal optimistic-allocation loss to the service.
            return false;
        }
        catch (DbException)
        {
            if (await GetByIdempotencyKeyAsync(r.IdempotencyKey, ct) is not null)
                return false;
            throw;
        }
    }

    private const string SelectSql = @"
        SELECT DISPOSITION_ID AS DispositionId, PLANT_ID AS PlantId, LOT_ID AS LotId,
               WORK_ORDER_ID AS WorkOrderId, PROCESS_ID AS ProcessId,
               DEFECT_EXECUTION_ID AS DefectExecutionId, DEFECT_CODE AS DefectCode,
               DISPOSITION_TYPE AS DispositionType, QUANTITY AS Quantity,
               REASON_CODE AS ReasonCode, REASON AS Reason, DECIDED_BY AS DecidedBy,
               DECIDED_AT AS DecidedAt, SOURCE_EXECUTION_ID AS SourceExecutionId,
               IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
               CLIENT_CHANNEL AS ClientChannel, DEVICE_ID AS DeviceId
        FROM POM_LOT_DISPOSITION";

    private const string ScopeSql = @"
        SELECT l.LOT_ID AS LotId, l.PLANT_ID AS PlantId, l.WORK_ORDER_ID AS WorkOrderId,
               COALESCE(d.PROCESS_ID, @processId) AS ProcessId,
               d.EXECUTION_ID AS DefectExecutionId,
               COALESCE(d.DEFECT_CODE, @defectCode) AS DefectCode,
               l.DEFECT_QTY AS LotDefectQuantity,
               COALESCE((SELECT SUM(x.QUANTITY) FROM POM_LOT_DISPOSITION x
                         WHERE x.LOT_ID = l.LOT_ID), 0) AS LotDisposedQuantity,
               CASE
                 WHEN @defectExecutionId IS NOT NULL THEN d.DEFECT_QTY
                  WHEN @defectCode IS NOT NULL THEN COALESCE((
                     SELECT SUM(e.DEFECT_QTY) FROM POM_LOT_DEFECT_EXECUTION e
                     WHERE e.LOT_ID = l.LOT_ID AND e.DEFECT_CODE = @defectCode
                       AND (@processId IS NULL OR e.PROCESS_ID = @processId)), 0)
                 ELSE l.DEFECT_QTY
               END AS EvidenceQuantity,
               CASE
                 WHEN @defectExecutionId IS NOT NULL THEN COALESCE((
                    SELECT SUM(x.QUANTITY) FROM POM_LOT_DISPOSITION x
                    WHERE x.DEFECT_EXECUTION_ID = @defectExecutionId), 0)
                  WHEN @defectCode IS NOT NULL THEN COALESCE((
                     SELECT SUM(x.QUANTITY) FROM POM_LOT_DISPOSITION x
                     WHERE x.LOT_ID = l.LOT_ID AND x.DEFECT_CODE = @defectCode
                       AND (@processId IS NULL OR x.PROCESS_ID = @processId)), 0)
                 ELSE COALESCE((SELECT SUM(x.QUANTITY) FROM POM_LOT_DISPOSITION x
                    WHERE x.LOT_ID = l.LOT_ID), 0)
               END AS EvidenceDisposedQuantity
        FROM POM_LOT l
        LEFT JOIN POM_LOT_DEFECT_EXECUTION d
          ON d.EXECUTION_ID = @defectExecutionId
         AND d.LOT_ID = l.LOT_ID
         AND (@processId IS NULL OR d.PROCESS_ID = @processId)
         AND (@defectCode IS NULL OR d.DEFECT_CODE = @defectCode)
        WHERE l.LOT_ID = @lotId AND l.PLANT_ID = @plantId
          AND (@workOrderId IS NULL OR l.WORK_ORDER_ID = @workOrderId)
          AND (@defectExecutionId IS NULL OR d.EXECUTION_ID IS NOT NULL)
           AND (@defectExecutionId IS NOT NULL OR @defectCode IS NULL OR EXISTS (
               SELECT 1 FROM POM_LOT_DEFECT_EXECUTION e
               WHERE e.LOT_ID = l.LOT_ID AND e.DEFECT_CODE = @defectCode
                 AND (@processId IS NULL OR e.PROCESS_ID = @processId)))";

    // Touching UPDATED_AT acquires the LOT row/write lock and also reflects the new child business
    // event. A true no-op UPDATE can report zero affected rows on SQLite, so it cannot be the guard.
    // The following INSERT recalculates every allocation predicate while this lock is held.
    private const string GuardLotSql = @"
        UPDATE POM_LOT SET UPDATED_AT = @DecidedAt
        WHERE LOT_ID = @LotId AND PLANT_ID = @PlantId
          AND (@WorkOrderId IS NULL OR WORK_ORDER_ID = @WorkOrderId)
          AND NOT EXISTS (SELECT 1 FROM POM_LOT_DISPOSITION
                          WHERE IDEMPOTENCY_KEY = @IdempotencyKey)";

    private const string InsertSql = @"
        INSERT INTO POM_LOT_DISPOSITION
        (DISPOSITION_ID, PLANT_ID, LOT_ID, WORK_ORDER_ID, PROCESS_ID,
         DEFECT_EXECUTION_ID, DEFECT_CODE, DISPOSITION_TYPE, QUANTITY,
         REASON_CODE, REASON, DECIDED_BY, APPROVED_BY, DECIDED_AT,
         SOURCE_EXECUTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, CLIENT_CHANNEL,
         DEVICE_ID, CREATED_AT)
        VALUES
        (
         @DispositionId, @PlantId, @LotId, @WorkOrderId, @ProcessId,
         @DefectExecutionId, @DefectCode, @DispositionType, @Quantity,
         @ReasonCode, @Reason, @DecidedBy, NULL, @DecidedAt,
         @SourceExecutionId, @IdempotencyKey, @RequestHash, @ClientChannel,
         @DeviceId, @DecidedAt
        )";

    private sealed class ScopeRow
    {
        public string LotId { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? DefectExecutionId { get; set; }
        public string? DefectCode { get; set; }
        public decimal LotDefectQuantity { get; set; }
        public decimal LotDisposedQuantity { get; set; }
        public decimal EvidenceQuantity { get; set; }
        public decimal EvidenceDisposedQuantity { get; set; }

        public LotDispositionScope ToScope() => new(
            LotId, PlantId, WorkOrderId, ProcessId, DefectExecutionId, DefectCode,
            LotDefectQuantity, LotDisposedQuantity, EvidenceQuantity, EvidenceDisposedQuantity);
    }

    private sealed class AllocationConflictException : Exception
    {
    }

    private sealed class DispositionRow
    {
        public string DispositionId { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string LotId { get; set; } = "";
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? DefectExecutionId { get; set; }
        public string? DefectCode { get; set; }
        public string DispositionType { get; set; } = "";
        public decimal Quantity { get; set; }
        public string? ReasonCode { get; set; }
        public string Reason { get; set; } = "";
        public string DecidedBy { get; set; } = "";
        public DateTime DecidedAt { get; set; }
        public string? SourceExecutionId { get; set; }
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ClientChannel { get; set; } = "";
        public string? DeviceId { get; set; }

        public LotDispositionRecord ToRecord() => new(
            DispositionId, PlantId, LotId, WorkOrderId, ProcessId,
            DefectExecutionId, DefectCode, DispositionType, Quantity,
            ReasonCode, Reason, DecidedBy, DecidedAt, SourceExecutionId,
            IdempotencyKey, RequestHash, ClientChannel, DeviceId);
    }
}
