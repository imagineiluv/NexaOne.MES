using System.Data.Common;
using System.Globalization;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Infrastructure;

public sealed class MaterialLotRepository : QueryRepository, IMaterialLotRepository
{
    private readonly ServiceObjectProcessor _processor;

    public MaterialLotRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<MaterialLotState?> GetLotAsync(string lotId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT LOT_ID, MATERIAL_ID, LOT_NO, WAREHOUSE, CURRENT_QTY, UNIT, STATUS, VERSION_NO
            FROM IVT_MATERIAL_LOT WHERE LOT_ID = @lotId
            """;
        var row = await QueryFirstOrDefaultAsync<LotRow>(sql, new { lotId }, ct);
        return row?.ToDomain();
    }

    public Task<MaterialLotTransaction?> GetByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
        => GetTransactionAsync("IDEMPOTENCY_KEY = @idempotencyKey", new { idempotencyKey }, ct);

    public Task<MaterialLotTransaction?> GetBySourceEventAsync(
        string sourceSystem, string sourceEventId, CancellationToken ct = default)
        => GetTransactionAsync(
            "SOURCE_SYSTEM = @sourceSystem AND SOURCE_EVENT_ID = @sourceEventId",
            new { sourceSystem, sourceEventId }, ct);

    public async Task<bool> HasFeedSessionReservationAsync(
        string lotId,
        CancellationToken ct = default) =>
        await QueryFirstOrDefaultAsync<string>(
            """
            SELECT ACTIVE_FEED_SESSION_ID
              FROM IVT_MATERIAL_LOT
             WHERE LOT_ID = @lotId AND ACTIVE_FEED_SESSION_ID IS NOT NULL
            """,
            new { lotId }, ct) is not null;

    public async Task<bool> TryReceiveAsync(
        MaterialLotTransaction record, CancellationToken ct = default)
    {
        var p = Parameters(record);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                ("""
                 INSERT INTO IVT_MATERIAL_LOT
                   (LOT_ID, MATERIAL_ID, LOT_NO, WAREHOUSE, CURRENT_QTY, UNIT, STATUS,
                    RECEIVED_AT, EXPIRY_AT, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                 SELECT @LotId, @MaterialId, @LotNumber, @ToLocation, @BalanceAfter, @Unit,
                        @ResultStatus, @OccurredAt, @ExpiryAt, @ResultVersion,
                        @ActorId, @Now, @ActorId, @Now
                 WHERE @ExpectedVersion = 0
                   AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_LOT WHERE LOT_ID = @LotId)
                   AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_TX WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
                   AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_TX
                                   WHERE SOURCE_SYSTEM = @SourceSystem AND SOURCE_EVENT_ID = @SourceEventId)
                 """, p),
                (InsertTransactionSql, p));
        }
        catch (DbException)
        {
            if (await HasReplayAsync(record, ct)
                || await TransactionExistsAsync(record.TransactionId, ct)
                || await GetLotAsync(record.LotId, ct) is not null) return false;
            throw;
        }
    }

    public async Task<bool> TryApplyAsync(
        MaterialLotTransaction record, CancellationToken ct = default)
    {
        var p = Parameters(record);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                ("""
                 UPDATE IVT_MATERIAL_LOT SET
                   CURRENT_QTY = @BalanceAfter,
                   WAREHOUSE = @ToLocation,
                   STATUS = @ResultStatus,
                   VERSION_NO = @ResultVersion,
                   UPDATED_BY = @ActorId,
                   UPDATED_AT = @Now
                 WHERE LOT_ID = @LotId
                   AND MATERIAL_ID = @MaterialId
                   AND VERSION_NO = @ExpectedVersion
                   AND CAST(COALESCE(CURRENT_QTY, 0) AS DECIMAL(38,9)) =
                       CAST(@BalanceBefore AS DECIMAL(38,9))
                   AND STATUS = @PreviousStatus
                   AND COALESCE(WAREHOUSE, '') = COALESCE(@FromLocation, '')
                   AND ACTIVE_FEED_SESSION_ID IS NULL
                   AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_TX WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
                   AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_TX
                                   WHERE SOURCE_SYSTEM = @SourceSystem AND SOURCE_EVENT_ID = @SourceEventId)
                 """, p),
                (InsertTransactionSql, p));
        }
        catch (DbException)
        {
            if (await HasReplayAsync(record, ct)
                || await TransactionExistsAsync(record.TransactionId, ct)) return false;
            throw;
        }
    }

    private async Task<bool> HasReplayAsync(MaterialLotTransaction record, CancellationToken ct)
        => await GetByIdempotencyKeyAsync(record.IdempotencyKey, ct) is not null
           || await GetBySourceEventAsync(record.SourceSystem, record.SourceEventId, ct) is not null;

    private async Task<bool> TransactionExistsAsync(string transactionId, CancellationToken ct)
        => await QueryFirstOrDefaultAsync<string>(
            "SELECT TX_ID FROM IVT_MATERIAL_TX WHERE TX_ID = @transactionId",
            new { transactionId }, ct) is not null;

    private async Task<MaterialLotTransaction?> GetTransactionAsync(
        string predicate, object parameter, CancellationToken ct)
    {
        var sql = $"""
            SELECT TX_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TX_TYPE, LOT_ID, MATERIAL_ID, QTY,
                   BALANCE_BEFORE, BALANCE_AFTER, BALANCE_DELTA, FROM_WAREHOUSE, TO_WAREHOUSE,
                   RESULT_STATUS, EXPECTED_VERSION, RESULT_VERSION, TX_AT, PROCESSED_BY,
                   SOURCE_SYSTEM, SOURCE_EVENT_ID, CORRELATION_ID, REMARK, METADATA_JSON
            FROM IVT_MATERIAL_TX WHERE {predicate}
            """;
        var row = await QueryFirstOrDefaultAsync<TransactionRow>(sql, parameter, ct);
        return row?.ToDomain();
    }

    private const string InsertTransactionSql = """
        INSERT INTO IVT_MATERIAL_TX
          (TX_ID, LOT_ID, MATERIAL_ID, TX_TYPE, QTY, FROM_WAREHOUSE, TO_WAREHOUSE,
           TX_AT, PROCESSED_BY, STATUS, REMARK, IDEMPOTENCY_KEY, REQUEST_HASH,
           EXPECTED_VERSION, RESULT_VERSION, SOURCE_SYSTEM, SOURCE_EVENT_ID,
           CORRELATION_ID, METADATA_JSON, BALANCE_BEFORE, BALANCE_AFTER, BALANCE_DELTA,
           RESULT_STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
          (@TransactionId, @LotId, @MaterialId, @Operation, @Quantity, @FromLocation, @ToLocation,
           @OccurredAt, @ActorId, 'Completed', @Reason, @IdempotencyKey, @RequestHash,
           @ExpectedVersion, @ResultVersion, @SourceSystem, @SourceEventId,
           @CorrelationId, @MetadataJson, @BalanceBefore, @BalanceAfter, @BalanceDelta,
           @ResultStatus, @ActorId, @Now, @ActorId, @Now)
        """;

    private static DynamicParameters Parameters(MaterialLotTransaction record)
    {
        var p = new DynamicParameters(record);
        p.Add("Now", DateTime.UtcNow);
        p.Add("PreviousStatus", record.PreviousStatus);
        return p;
    }

    private static decimal ToDecimal(object? value) => value is null or DBNull
        ? 0m
        : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private sealed class LotRow
    {
        public string LotId { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public string? LotNo { get; set; }
        public string? Warehouse { get; set; }
        public object? CurrentQty { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int VersionNo { get; set; }

        public MaterialLotState ToDomain() => new(
            LotId, MaterialId, LotNo, Warehouse, ToDecimal(CurrentQty), Unit, Status, VersionNo);
    }

    private sealed class TransactionRow
    {
        public string TxId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string TxType { get; set; } = string.Empty;
        public string LotId { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public object? Qty { get; set; }
        public object? BalanceBefore { get; set; }
        public object? BalanceAfter { get; set; }
        public object? BalanceDelta { get; set; }
        public string? FromWarehouse { get; set; }
        public string? ToWarehouse { get; set; }
        public string ResultStatus { get; set; } = string.Empty;
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public DateTime TxAt { get; set; }
        public string ProcessedBy { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceEventId { get; set; } = string.Empty;
        public string? CorrelationId { get; set; }
        public string? Remark { get; set; }
        public string? MetadataJson { get; set; }

        public MaterialLotTransaction ToDomain() => new(
            TxId, IdempotencyKey, RequestHash, TxType, LotId, MaterialId, ToDecimal(Qty),
            ToDecimal(BalanceBefore), ToDecimal(BalanceAfter), ToDecimal(BalanceDelta),
            FromWarehouse, ToWarehouse, string.Empty, ResultStatus, ExpectedVersion, ResultVersion, TxAt,
            ProcessedBy, SourceSystem, SourceEventId, CorrelationId, Remark, MetadataJson);
    }
}
