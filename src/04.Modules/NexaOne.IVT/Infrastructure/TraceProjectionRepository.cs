using Dapper;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaDB.Data.Abstractions.Interfaces;

namespace NexaOne.IVT.Infrastructure;

/// <summary>
/// Durable IVT database adapter for TRACE projection. It never subscribes to the realtime bus and
/// only owns binding, inbox, lease, calculator state, and feed-session persistence.
/// </summary>
public sealed class TraceProjectionRepository : QueryRepository, ITraceProjectionRepository
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;
    private readonly string _stateUpsertSql;

    public TraceProjectionRepository(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect)
        : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _stateUpsertSql = _dialect.BuildUpsertSql(
            "IVT_TRACE_PROJECTION_STATE",
            new[] { "BINDING_ID" },
            new[]
            {
                "LAST_COLLECT_ID", "LAST_VALUE", "LAST_COLLECTED_AT",
                "UPDATED_BY", "UPDATED_AT",
            },
            new[] { "CREATED_BY", "CREATED_AT" });
    }

    public async Task<IReadOnlyList<TraceProjectionBinding>> GetSourceBindingsAsync(
        CancellationToken ct = default)
    {
        const string bindingSql = @"
            SELECT B.BINDING_ID AS BindingId,
                   B.PLANT_ID AS PlantId,
                   B.EQUIPMENT_ID AS EquipmentId,
                   B.PARAMETER_ID AS ParameterId,
                   B.FEED_POINT_ID AS FeedPointId,
                   B.CALCULATION_MODE AS CalculationMode,
                   B.SCALE_FACTOR AS ScaleFactor,
                   B.PULSE_QUANTITY AS PulseQuantity,
                   B.OUTPUT_UNIT AS OutputUnit,
                   B.EFFECTIVE_FROM AS EffectiveFrom,
                   B.EFFECTIVE_TO AS EffectiveTo,
                   C.LAST_COLLECT_ID AS LastCollectId,
                   C.LAST_COLLECTED_AT AS LastCollectedAt
              FROM IVT_TRACE_CONSUMPTION_BINDING B
              LEFT JOIN IVT_TRACE_INGESTION_CURSOR C
                ON C.BINDING_ID = B.BINDING_ID
             WHERE B.IS_ACTIVE = 1";

        var bindingRows = await QueryAsync<BindingRow>(bindingSql, null, ct);
        if (bindingRows.Count == 0) return Array.Empty<TraceProjectionBinding>();
        return bindingRows.Select(row => row.ToDomain()).ToList();
    }

    public async Task<int> AddToInboxAsync(
        IReadOnlyCollection<TraceProjectionItem> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;

        const string insertSql = @"
            INSERT INTO IVT_TRACE_PROJECTION_INBOX
            (BINDING_ID, COLLECT_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
             CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT, RAW_VALUE,
             QUALITY, COLLECTED_AT, STATUS, ATTEMPT_COUNT, CREATED_BY, CREATED_AT,
             UPDATED_BY, UPDATED_AT, IS_WORK_ITEM)
            SELECT @BindingId, @CollectId, @PlantId, @EquipmentId, @ParameterId, @FeedPointId,
                   @CalculationMode, @ScaleFactor, @PulseQuantity, @OutputUnit, @RawValue,
                   @Quality, @CollectedAt, 'Pending', 0, 'SYSTEM', @Now, 'SYSTEM', @Now, 1
            WHERE NOT EXISTS (
                SELECT 1 FROM IVT_TRACE_PROJECTION_INBOX
                WHERE BINDING_ID = @BindingId AND COLLECT_ID = @CollectId)";
        const string updateCursorSql = @"
            UPDATE IVT_TRACE_INGESTION_CURSOR SET
                LAST_COLLECT_ID = @CollectId,
                LAST_COLLECTED_AT = @CollectedAt,
                UPDATED_BY = 'SYSTEM',
                UPDATED_AT = @Now
            WHERE BINDING_ID = @BindingId
              AND (LAST_COLLECTED_AT < @CollectedAt
                   OR (LAST_COLLECTED_AT = @CollectedAt AND LAST_COLLECT_ID < @CollectId))
              AND EXISTS (
                  SELECT 1 FROM IVT_TRACE_PROJECTION_INBOX
                  WHERE BINDING_ID = @BindingId
                    AND COLLECT_ID = @CollectId
                    AND COLLECTED_AT = @CollectedAt)";
        const string insertCursorSql = @"
            INSERT INTO IVT_TRACE_INGESTION_CURSOR
                (BINDING_ID, LAST_COLLECT_ID, LAST_COLLECTED_AT,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @BindingId, @CollectId, @CollectedAt,
                   'SYSTEM', @Now, 'SYSTEM', @Now
            WHERE EXISTS (
                  SELECT 1 FROM IVT_TRACE_PROJECTION_INBOX
                  WHERE BINDING_ID = @BindingId
                    AND COLLECT_ID = @CollectId
                    AND COLLECTED_AT = @CollectedAt)
              AND NOT EXISTS (
                  SELECT 1 FROM IVT_TRACE_INGESTION_CURSOR
                  WHERE BINDING_ID = @BindingId)";

        var uniqueItems = items
            .DistinctBy(item => (item.BindingId, item.CollectId))
            .ToArray();
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>(uniqueItems.Length * 3);
        foreach (var item in uniqueItems)
        {
            var param = new
            {
                item.BindingId,
                item.CollectId,
                item.PlantId,
                item.EquipmentId,
                item.ParameterId,
                item.FeedPointId,
                item.CalculationMode,
                item.ScaleFactor,
                item.PulseQuantity,
                item.OutputUnit,
                item.RawValue,
                item.Quality,
                item.CollectedAt,
                Now = now,
            };
            statements.Add((insertSql, param));
            statements.Add((updateCursorSql, param));
            statements.Add((insertCursorSql, param));
        }

        IReadOnlyList<int> results;
        try
        {
            results = await _processor.ExecuteManyWithResultsAsync(ct, statements.ToArray());
        }
        catch (DbException exception) when (IsCursorIdentityRace(exception))
        {
            // Two hosts can observe a missing binding cursor concurrently. The loser transaction is
            // rolled back in full; replay once after the winner's PK row becomes visible.
            results = await _processor.ExecuteManyWithResultsAsync(ct, statements.ToArray());
        }

        var inserted = 0;
        for (var index = 0; index < results.Count; index += 3)
            inserted += results[index];
        return inserted;
    }

    public async Task<IReadOnlyList<TraceProjectionItem>> GetPendingAsync(
        int batchSize,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(batchSize, 1, 5000);
        var sql = _dialect.WrapPaged(
            @"SELECT BINDING_ID AS BindingId,
                     COLLECT_ID AS CollectId,
                     PLANT_ID AS PlantId,
                     EQUIPMENT_ID AS EquipmentId,
                     PARAMETER_ID AS ParameterId,
                     FEED_POINT_ID AS FeedPointId,
                     CALCULATION_MODE AS CalculationMode,
                     SCALE_FACTOR AS ScaleFactor,
                     PULSE_QUANTITY AS PulseQuantity,
                     OUTPUT_UNIT AS OutputUnit,
                     RAW_VALUE AS RawValue,
                     QUALITY AS Quality,
                     COLLECTED_AT AS CollectedAt
              FROM IVT_TRACE_PROJECTION_INBOX
              WHERE IS_WORK_ITEM = 1
                AND STATUS IN ('Pending', 'Error')",
            "COLLECTED_AT, COLLECT_ID, BINDING_ID",
            0,
            limit);
        var rows = await QueryAsync<InboxRow>(sql, null, ct);
        if (rows.Count == 0) return Array.Empty<TraceProjectionItem>();

        var claimed = new List<TraceProjectionItem>(rows.Count);
        foreach (var bindingRows in rows.GroupBy(row => row.BindingId, StringComparer.OrdinalIgnoreCase))
        {
            var leaseOwnerId = await TryAcquireLeaseAsync(bindingRows.Key, ct);
            if (leaseOwnerId is null) continue;
            claimed.AddRange(bindingRows.Select(row => row.ToDomain() with { LeaseOwnerId = leaseOwnerId }));
        }

        return claimed
            .OrderBy(item => item.CollectedAt)
            .ThenBy(item => item.CollectId, StringComparer.Ordinal)
            .ThenBy(item => item.BindingId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<TraceProjectionState?> GetStateAsync(
        string bindingId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT BINDING_ID AS BindingId,
                                    LAST_COLLECT_ID AS LastCollectId,
                                    LAST_VALUE AS LastValue,
                                    LAST_COLLECTED_AT AS LastCollectedAt
                             FROM IVT_TRACE_PROJECTION_STATE
                             WHERE BINDING_ID = @bindingId";
        var row = await QueryFirstOrDefaultAsync<StateRow>(sql, new { bindingId }, ct);
        return row is null
            ? null
            : new TraceProjectionState(
                row.BindingId, row.LastCollectId, ToDecimal(row.LastValue), row.LastCollectedAt);
    }

    public async Task<IReadOnlyList<MaterialFeedSession>> GetFeedSessionsAsync(
        string plantId,
        string equipmentId,
        string feedPointId,
        DateTime collectedAt,
        CancellationToken ct = default)
    {
        var sql = _dialect.WrapPaged(
            @"SELECT FEED_SESSION_ID AS FeedSessionId,
                     PLANT_ID AS PlantId,
                     EQUIPMENT_ID AS EquipmentId,
                     FEED_POINT_ID AS FeedPointId,
                     MATERIAL_LOT_ID AS MaterialLotId,
                     MATERIAL_ID AS MaterialId,
                     PROCESS_LOT_ID AS ProcessLotId,
                     WORK_ORDER_ID AS WorkOrderId,
                     PROCESS_ID AS ProcessId,
                     RECIPE_ID AS RecipeId,
                     RECIPE_VERSION AS RecipeVersion,
                     MOUNTED_BY AS MountedBy,
                     MOUNTED_AT AS MountedAt,
                     UNMOUNTED_AT AS UnmountedAt
              FROM IVT_MATERIAL_FEED_SESSION
              WHERE PLANT_ID = @plantId
                AND EQUIPMENT_ID = @equipmentId
                AND FEED_POINT_ID = @feedPointId
                AND STATUS <> 'Cancelled'
                AND MOUNTED_AT <= @collectedAt
                AND (UNMOUNTED_AT IS NULL OR UNMOUNTED_AT > @collectedAt)",
            "MOUNTED_AT DESC, FEED_SESSION_ID",
            0,
            2);
        var rows = await QueryAsync<FeedSessionRow>(
            sql, new { plantId, equipmentId, feedPointId, collectedAt }, ct);
        return rows.Select(row => row.ToDomain()).ToList();
    }

    public async Task CompleteAsync(
        TraceProjectionItem item,
        TraceProjectionState? nextState,
        string status,
        string? consumptionId,
        string? detail,
        CancellationToken ct = default)
    {
        if (status is not ("Applied" or "Ignored"))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Terminal status must be Applied or Ignored.");

        var now = DateTime.UtcNow;
        var inboxParam = new
        {
            item.BindingId,
            item.CollectId,
            Status = status,
            ConsumptionId = consumptionId,
            Detail = TrimError(detail),
            LeaseOwnerId = RequireLease(item),
            Now = now,
        };
        const string completeSql = @"
            UPDATE IVT_TRACE_PROJECTION_INBOX SET
                STATUS = @Status,
                ATTEMPT_COUNT = ATTEMPT_COUNT + 1,
                LAST_ERROR = @Detail,
                CONSUMPTION_ID = @ConsumptionId,
                PROCESSED_AT = @Now,
                IS_WORK_ITEM = 0,
                UPDATED_BY = 'SYSTEM',
                UPDATED_AT = @Now
            WHERE BINDING_ID = @BindingId AND COLLECT_ID = @CollectId
              AND STATUS IN ('Pending', 'Error')
              AND EXISTS (
                  SELECT 1 FROM IVT_TRACE_PROJECTION_LEASE L
                  WHERE L.BINDING_ID = @BindingId
                    AND L.OWNER_ID = @LeaseOwnerId
                    AND L.LEASE_UNTIL > @Now)";

        if (nextState is null)
        {
            var completed = await _processor.ExecuteGuardedManyAsync(ct, (completeSql, inboxParam));
            if (!completed) throw LeaseLost(item);
            return;
        }

        var stateParam = new DynamicParameters();
        stateParam.Add("BINDING_ID", nextState.BindingId);
        stateParam.Add("LAST_COLLECT_ID", nextState.LastCollectId);
        stateParam.Add("LAST_VALUE", nextState.LastValue);
        stateParam.Add("LAST_COLLECTED_AT", nextState.LastCollectedAt);
        stateParam.Add("CREATED_BY", "SYSTEM");
        stateParam.Add("CREATED_AT", now);
        stateParam.Add("UPDATED_BY", "SYSTEM");
        stateParam.Add("UPDATED_AT", now);

        var stateCompleted = await _processor.ExecuteGuardedManyAsync(
            ct,
            (completeSql, inboxParam),
            (_stateUpsertSql, stateParam));
        if (!stateCompleted) throw LeaseLost(item);
    }

    public async Task MarkErrorAsync(
        TraceProjectionItem item,
        string error,
        CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE IVT_TRACE_PROJECTION_INBOX SET
                STATUS = 'Error',
                ATTEMPT_COUNT = ATTEMPT_COUNT + 1,
                LAST_ERROR = @Error,
                UPDATED_BY = 'SYSTEM',
                UPDATED_AT = @Now
            WHERE BINDING_ID = @BindingId AND COLLECT_ID = @CollectId
              AND STATUS IN ('Pending', 'Error')
              AND EXISTS (
                  SELECT 1 FROM IVT_TRACE_PROJECTION_LEASE L
                  WHERE L.BINDING_ID = @BindingId
                    AND L.OWNER_ID = @LeaseOwnerId
                    AND L.LEASE_UNTIL > @Now)";
        var marked = await _processor.ExecuteGuardedManyAsync(ct, (sql, new
        {
            item.BindingId,
            item.CollectId,
            LeaseOwnerId = RequireLease(item),
            Error = TrimError(error) ?? "Unknown projection error.",
            Now = DateTime.UtcNow,
        }));
        if (!marked) throw LeaseLost(item);
    }

    public async Task ReleaseLeaseAsync(
        string bindingId,
        string leaseOwnerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bindingId) || string.IsNullOrWhiteSpace(leaseOwnerId)) return;
        const string sql = @"DELETE FROM IVT_TRACE_PROJECTION_LEASE
                             WHERE BINDING_ID = @bindingId AND OWNER_ID = @leaseOwnerId";
        await _processor.ExecuteAsync(sql, new { bindingId, leaseOwnerId }, ct);
    }

    private async Task<string?> TryAcquireLeaseAsync(
        string bindingId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        if (ownerId.Length > 100) ownerId = ownerId[^100..];
        var param = new
        {
            BindingId = bindingId,
            OwnerId = ownerId,
            Now = now,
            LeaseUntil = now.Add(LeaseDuration),
        };

        const string takeExpired = @"
            UPDATE IVT_TRACE_PROJECTION_LEASE SET
                OWNER_ID = @OwnerId,
                ACQUIRED_AT = @Now,
                LEASE_UNTIL = @LeaseUntil,
                UPDATED_BY = 'SYSTEM',
                UPDATED_AT = @Now
            WHERE BINDING_ID = @BindingId AND LEASE_UNTIL <= @Now";
        if (await _processor.ExecuteAsync(takeExpired, param, ct) == 1) return ownerId;

        const string insert = @"
            INSERT INTO IVT_TRACE_PROJECTION_LEASE
                (BINDING_ID, OWNER_ID, ACQUIRED_AT, LEASE_UNTIL,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @BindingId, @OwnerId, @Now, @LeaseUntil,
                   'SYSTEM', @Now, 'SYSTEM', @Now
            WHERE NOT EXISTS (
                SELECT 1 FROM IVT_TRACE_PROJECTION_LEASE WHERE BINDING_ID = @BindingId)";
        try
        {
            return await _processor.ExecuteAsync(insert, param, ct) == 1 ? ownerId : null;
        }
        catch (DbException exception) when (IsLeaseIdentityRace(exception))
        {
            // Another host won the binding PK race after our NOT EXISTS read.
            return null;
        }
    }

    internal static bool IsLeaseIdentityRace(DbException exception)
    {
        var uniqueViolation = exception switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(
                    exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException",
                    StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!uniqueViolation) return false;

        return exception.Message.Contains(
                   "PK_IVT_TRACE_PROJECTION_LEASE",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "IVT_TRACE_PROJECTION_LEASE.BINDING_ID",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCursorIdentityRace(DbException exception)
    {
        var uniqueViolation = exception switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(
                    exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException",
                    StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!uniqueViolation) return false;

        return exception.Message.Contains(
                   "PK_IVT_TRACE_INGESTION_CURSOR",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "IVT_TRACE_INGESTION_CURSOR.BINDING_ID",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireLease(TraceProjectionItem item)
        => string.IsNullOrWhiteSpace(item.LeaseOwnerId)
            ? throw new InvalidOperationException(
                $"TRACE inbox '{item.BindingId}/{item.CollectId}' was not claimed by this worker.")
            : item.LeaseOwnerId;

    private static DBConcurrencyException LeaseLost(TraceProjectionItem item) => new(
        $"TRACE projection lease was lost for '{item.BindingId}/{item.CollectId}'. The row will be retried.");

    private static string? TrimError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim();
        return clean.Length <= 1000 ? clean : clean[..1000];
    }

    private static decimal ToDecimal(object? value) =>
        value is null or DBNull
            ? 0m
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static decimal? ToNullableDecimal(object? value) =>
        value is null or DBNull
            ? null
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private sealed class BindingRow
    {
        public string BindingId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string ParameterId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string CalculationMode { get; set; } = string.Empty;
        public object? ScaleFactor { get; set; }
        public object? PulseQuantity { get; set; }
        public string OutputUnit { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string? LastCollectId { get; set; }
        public DateTime? LastCollectedAt { get; set; }

        public TraceProjectionBinding ToDomain() => new(
            BindingId,
            PlantId,
            EquipmentId,
            ParameterId,
            FeedPointId,
            CalculationMode,
            ToDecimal(ScaleFactor),
            ToNullableDecimal(PulseQuantity),
            OutputUnit,
            EffectiveFrom,
            EffectiveTo,
            LastCollectedAt,
            LastCollectId);
    }

    private sealed class InboxRow
    {
        public string BindingId { get; set; } = string.Empty;
        public string CollectId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string ParameterId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string CalculationMode { get; set; } = string.Empty;
        // SQLite returns DECIMAL affinity as Int64 or Double depending on the stored value.
        // Receive provider values as object and normalize at the repository boundary.
        public object? ScaleFactor { get; set; }
        public object? PulseQuantity { get; set; }
        public string OutputUnit { get; set; } = string.Empty;
        public object? RawValue { get; set; }
        public string Quality { get; set; } = string.Empty;
        public DateTime CollectedAt { get; set; }

        public TraceProjectionItem ToDomain() => new(
            BindingId, CollectId, PlantId, EquipmentId, ParameterId, FeedPointId,
            CalculationMode, ToDecimal(ScaleFactor), ToNullableDecimal(PulseQuantity),
            OutputUnit, ToDecimal(RawValue), Quality, CollectedAt);
    }

    private sealed class StateRow
    {
        public string BindingId { get; set; } = string.Empty;
        public string LastCollectId { get; set; } = string.Empty;
        public object? LastValue { get; set; }
        public DateTime LastCollectedAt { get; set; }
    }

    private sealed class FeedSessionRow
    {
        public string FeedSessionId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string MaterialLotId { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public string MountedBy { get; set; } = string.Empty;
        public DateTime MountedAt { get; set; }
        public DateTime? UnmountedAt { get; set; }

        public MaterialFeedSession ToDomain() => new(
            FeedSessionId, PlantId, EquipmentId, FeedPointId, MaterialLotId, MaterialId,
            ProcessLotId, WorkOrderId, ProcessId, RecipeId, RecipeVersion, MountedBy,
            MountedAt, UnmountedAt);
    }
}
