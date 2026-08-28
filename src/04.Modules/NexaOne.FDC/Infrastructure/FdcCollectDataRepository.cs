using System.Globalization;
using System.Diagnostics;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcCollectDataRepository : QueryRepository,
    IFdcCollectDataRepository,
    IFdcCollectDataRetentionRepository,
    IFdcTraceRetentionStateRepository
{
    // Each DELETE owns one short transaction. A single maintenance invocation is also bounded so
    // continuous back-dated ingestion cannot monopolize the writer indefinitely; the next run
    // resumes from the same indexed cutoff path.
    internal const int DefaultRetentionBatchSize = 1_000;
    internal const int DefaultMaxRetentionBatchesPerCall = 100;

    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;
    private readonly DatabaseProviderKind _providerKind;
    private readonly int _retentionBatchSize;
    private readonly int _maxRetentionBatchesPerCall;

    public FdcCollectDataRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect)
        : this(dataSource, dialect, DefaultRetentionBatchSize, DefaultMaxRetentionBatchesPerCall)
    {
    }

    internal FdcCollectDataRepository(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect,
        int retentionBatchSize,
        int maxRetentionBatchesPerCall) : base(dataSource)
    {
        if (retentionBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(retentionBatchSize));
        if (maxRetentionBatchesPerCall <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetentionBatchesPerCall));
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
        _providerKind = dataSource.Provider.Kind;
        _retentionBatchSize = retentionBatchSize;
        _maxRetentionBatchesPerCall = maxRetentionBatchesPerCall;
    }

    public async Task<IReadOnlyList<FdcCollectData>> GetByParameterAsync(
        string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        // 행 상한(@limit)을 TOP/LIMIT로 SQL에 밀어 무제한 시계열 조회를 방어한다(GetLatestAsync와 동일 페이징).
        // 범위가 상한을 넘으면 최신 @limit건을 가져온 뒤 Reverse로 시간 오름차순(기존 출력 계약)을 복원한다.
        var rangePredicate = _providerKind == DatabaseProviderKind.Sqlite
            ? "COLLECTED_AT >= SUBSTR(@from, 1, 19) "
              + $"AND {SqliteTimestampKey("COLLECTED_AT")} >= @from "
              + "AND COLLECTED_AT <= @to "
              + $"AND {SqliteTimestampKey("COLLECTED_AT")} <= @to"
            : "COLLECTED_AT >= @from AND COLLECTED_AT <= @to";
        var sql = _dialect.WrapPaged(
            $"SELECT * FROM FDC_COLLECT_DATA "
            + "WHERE PARAMETER_ID = @parameterId "
            + $"AND {rangePredicate}",
            "COLLECTED_AT DESC", 0, limit);
        var rows = await QueryAsync<DataRow>(sql, new
        {
            parameterId,
            from = DbTimestamp(from),
            to = DbTimestamp(to),
        }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcCollectData>().Reverse().ToList();
    }

    public async Task<IReadOnlyList<FdcCollectData>> GetLatestAsync(
        string parameterId, int limit, CancellationToken ct = default)
    {
        var sql = _dialect.WrapPaged(
            "SELECT * FROM FDC_COLLECT_DATA WHERE PARAMETER_ID = @parameterId",
            "COLLECTED_AT DESC", 0, limit);
        var rows = await QueryAsync<DataRow>(sql, new { parameterId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcCollectData>().Reverse().ToList();
    }

    public async Task<IReadOnlyList<FdcCollectData>> GetTraceAsync(
        string equipmentId,
        string parameterId,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        DateTime? afterCollectedAt,
        string? afterCollectId,
        int limit,
        CancellationToken ct = default)
    {
        var hasCursor = afterCollectedAt.HasValue;
        var seekFrom = hasCursor
            && NormalizeUtcTimestamp(afterCollectedAt!.Value) > NormalizeUtcTimestamp(effectiveFrom)
                ? afterCollectedAt.Value
                : effectiveFrom;
        var sqliteCursorPredicate = hasCursor
            ? $" AND ({SqliteTimestampKey("COLLECTED_AT")} > @afterCollectedAt "
              + $"OR ({SqliteTimestampKey("COLLECTED_AT")} = @afterCollectedAt "
              + "AND COLLECT_ID > @afterCollectId))"
            : string.Empty;
        var mssqlCursorPredicate = hasCursor
            ? " AND (COLLECTED_AT > @afterCollectedAt "
              + "OR (COLLECTED_AT = @afterCollectedAt AND COLLECT_ID > @afterCollectId))"
            : string.Empty;
        var tracePredicate = _providerKind == DatabaseProviderKind.Sqlite
            ? "COLLECTED_AT >= SUBSTR(@seekFrom, 1, 19) "
              + $"AND {SqliteTimestampKey("COLLECTED_AT")} >= @seekFrom "
              + "AND (@effectiveTo IS NULL OR (COLLECTED_AT < @effectiveTo "
              + $"AND {SqliteTimestampKey("COLLECTED_AT")} < @effectiveTo))"
              + sqliteCursorPredicate
            : "COLLECTED_AT >= @seekFrom "
              + "AND (@effectiveTo IS NULL OR COLLECTED_AT < @effectiveTo)"
              + mssqlCursorPredicate;
        var traceOrder = _providerKind == DatabaseProviderKind.Sqlite
            ? $"{SqliteTimestampKey("COLLECTED_AT")}, COLLECT_ID"
            : "COLLECTED_AT, COLLECT_ID";
        var sql = _dialect.WrapPaged(
            "SELECT * FROM FDC_COLLECT_DATA "
            + "WHERE EQUIPMENT_ID = @equipmentId "
            + "AND PARAMETER_ID = @parameterId "
            + $"AND {tracePredicate}",
            traceOrder,
            0,
            Math.Clamp(limit, 1, 5000));
        var rows = await QueryAsync<DataRow>(sql, new
        {
            equipmentId,
            parameterId,
            seekFrom = DbTimestamp(seekFrom),
            effectiveTo = DbTimestamp(effectiveTo),
            afterCollectedAt = DbTimestamp(afterCollectedAt),
            afterCollectId,
        }, ct);
        return rows.Select(row => row.ToDomain()).OfType<FdcCollectData>().ToList();
    }

    // FDC_COLLECT_DATA는 감사 컬럼이 없어 8개 도메인 컬럼만 INSERT한다(감사 미주입 경로로 충분).
    private const string InsertSql = @"INSERT INTO FDC_COLLECT_DATA
            (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY, LOWER_LIMIT, UPPER_LIMIT)
            VALUES
            (@CollectId, @EquipmentId, @ParameterId, @Value, @CollectedAt, @Quality, @LowerLimit, @UpperLimit)";

    public async Task AddAsync(FdcCollectData data, CancellationToken ct = default)
    {
        await _processor.InsertAsync(InsertSql, InsertParameters(data), ct);
    }

    [Obsolete("Use FdcCollectDataRetentionWorker.")]
    public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default) =>
        Task.FromException<int>(new InvalidOperationException(
            "Legacy FDC retention deletion is disabled because it cannot enforce the IVT low-watermark guard."));

    async Task<FdcRetentionPurgeResult> IFdcCollectDataRetentionRepository.PurgeOlderThanAsync(
        DateTime cutoff,
        CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        // 보존정리: IX_FDC_COLLECT_RETENTION(COLLECTED_AT, COLLECT_ID)에서 결정적 후보만 뽑아
        // MSSQL/SQLite 모두 짧은 독립 트랜잭션으로 삭제한다. 한 번의 무제한 DELETE는 SQL Server log와
        // SQLite writer lock을 장시간 점유하므로 금지한다.
        var cutoffParameter = DbTimestamp(cutoff);
        var collectedBeforeCutoff = _providerKind == DatabaseProviderKind.Sqlite
            ? "COLLECTED_AT < @cutoff AND "
              + "CASE WHEN LENGTH(COLLECTED_AT)=19 THEN COLLECTED_AT || '.0000000' "
              + "ELSE SUBSTR(COLLECTED_AT || '0000000', 1, 27) END < @cutoff"
            : "COLLECTED_AT < @cutoff";
        var boundaryAtOrBeyondCutoff = _providerKind == DatabaseProviderKind.Sqlite
            ? "CASE WHEN LENGTH(COMPLETENESS_BOUNDARY)=19 "
              + "THEN COMPLETENESS_BOUNDARY || '.0000000' "
              + "ELSE SUBSTR(COMPLETENESS_BOUNDARY || '0000000', 1, 27) END >= @cutoff"
            : "COMPLETENESS_BOUNDARY >= @cutoff";
        var candidates = _dialect.WrapPaged(
            $"SELECT COLLECT_ID FROM FDC_COLLECT_DATA WHERE {collectedBeforeCutoff}",
            "COLLECTED_AT, COLLECT_ID",
            offset: 0,
            limit: _retentionBatchSize);
        var sql = $"""
            DELETE FROM FDC_COLLECT_DATA
             WHERE {collectedBeforeCutoff}
               AND EXISTS (
                   SELECT 1
                     FROM FDC_TRACE_RETENTION_STATE
                    WHERE STATE_ID = 'GLOBAL'
                      AND {boundaryAtOrBeyondCutoff})
               AND COLLECT_ID IN ({candidates})
            """;
        var advanceBoundaryComparison = _providerKind == DatabaseProviderKind.Sqlite
            ? "CASE WHEN LENGTH(COMPLETENESS_BOUNDARY)=19 "
              + "THEN COMPLETENESS_BOUNDARY || '.0000000' "
              + "ELSE SUBSTR(COMPLETENESS_BOUNDARY || '0000000', 1, 27) END < @cutoff"
            : "COMPLETENESS_BOUNDARY < @cutoff";
        var advanceBoundarySql = $"""
            UPDATE FDC_TRACE_RETENTION_STATE
               SET COMPLETENESS_BOUNDARY = CASE
                       WHEN COMPLETENESS_BOUNDARY IS NULL OR {advanceBoundaryComparison}
                           THEN @cutoff
                       ELSE COMPLETENESS_BOUNDARY
                   END,
                   UPDATED_BY = 'SYSTEM',
                   UPDATED_AT = @now
             WHERE STATE_ID = 'GLOBAL'
            """;

        var total = 0;
        for (var batch = 0; batch < _maxRetentionBatchesPerCall; batch++)
        {
            ct.ThrowIfCancellationRequested();
            var results = await _processor.ExecuteManyWithResultsAsync(
                ct,
                (advanceBoundarySql, new { cutoff = cutoffParameter, now = DateTime.UtcNow }),
                (sql, new { cutoff = cutoffParameter }));
            if (results[0] != 1)
            {
                throw new InvalidOperationException(
                    "FDC TRACE retention state GLOBAL row is missing; purge was refused.");
            }

            var deleted = results[1];
            total = checked(total + deleted);
            if (deleted < _retentionBatchSize)
                break;
        }

        DateTime? oldestRemaining = null;
        if (total == checked(_retentionBatchSize * _maxRetentionBatchesPerCall))
        {
            oldestRemaining = await QueryFirstOrDefaultAsync<DateTime?>(
                $"SELECT MIN(COLLECTED_AT) FROM FDC_COLLECT_DATA WHERE {collectedBeforeCutoff}",
                new { cutoff = cutoffParameter },
                ct);
        }

        elapsed.Stop();
        return new FdcRetentionPurgeResult(
            total,
            BatchLimitReached: oldestRemaining is not null,
            OldestRemainingCollectedAt: oldestRemaining,
            Elapsed: elapsed.Elapsed);
    }

    public async Task<FdcTraceRetentionState> GetTraceRetentionStateAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT COMPLETENESS_BOUNDARY AS CompletenessBoundary
              FROM FDC_TRACE_RETENTION_STATE
             WHERE STATE_ID = 'GLOBAL'
            """;
        var row = await QueryFirstOrDefaultAsync<RetentionStateRow>(sql, null, ct)
            ?? throw new InvalidOperationException(
                "FDC TRACE retention state GLOBAL row is missing.");
        var boundary = row.CompletenessBoundary
            ?? throw new InvalidOperationException(
                "FDC TRACE retention state GLOBAL boundary is missing.");
        if (boundary.Kind != DateTimeKind.Utc)
            boundary = DateTime.SpecifyKind(boundary, DateTimeKind.Utc);
        return new FdcTraceRetentionState(boundary);
    }

    public async Task AddBatchAsync(IEnumerable<FdcCollectData> data, CancellationToken ct = default)
    {
        // N건을 단일 트랜잭션에서 일괄 INSERT한다 — 행마다 별도 트랜잭션을 여는 N회 InsertAsync(원자성·왕복 손실)
        // 대신 ExecuteManyAsync로 한 트랜잭션에 묶어, 한 건이라도 실패하면 전체가 롤백되어 부분 적재를 막는다.
        var statements = data
            .Select(item => (Sql: InsertSql, Param: (object?)InsertParameters(item)))
            .ToArray();
        if (statements.Length == 0) return;
        await _processor.ExecuteManyAsync(ct, statements);
    }

    private object InsertParameters(FdcCollectData data)
    {
        var row = DataRow.FromDomain(data);
        if (_providerKind != DatabaseProviderKind.Sqlite) return row;
        return new
        {
            row.CollectId,
            row.EquipmentId,
            row.ParameterId,
            row.Value,
            CollectedAt = FormatSqliteUtcTimestamp(row.CollectedAt),
            row.Quality,
            row.LowerLimit,
            row.UpperLimit,
        };
    }

    private object DbTimestamp(DateTime value) =>
        _providerKind == DatabaseProviderKind.Sqlite
            ? FormatSqliteUtcTimestamp(value)
            : value;

    private object? DbTimestamp(DateTime? value) => value is null ? null : DbTimestamp(value.Value);

    private static string FormatSqliteUtcTimestamp(DateTime value)
    {
        var utc = NormalizeUtcTimestamp(value);
        return utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    }

    private static DateTime NormalizeUtcTimestamp(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static string SqliteTimestampKey(string expression) =>
        $"CASE WHEN LENGTH({expression})=19 THEN {expression} || '.0000000' "
        + $"ELSE SUBSTR({expression} || '0000000', 1, 27) END";

    private sealed class DataRow
    {
        public string  CollectId   { get; set; } = "";
        public string  EquipmentId { get; set; } = "";
        public string  ParameterId { get; set; } = "";
        public object? Value       { get; set; }
        public DateTime CollectedAt { get; set; }
        public string  Quality     { get; set; } = "Good";
        public object? LowerLimit  { get; set; }
        public object? UpperLimit  { get; set; }

        public FdcCollectData? ToDomain() =>
            FdcCollectData.Create(
                CollectId,
                EquipmentId,
                ParameterId,
                ToDecimal(Value),
                CollectedAt,
                Quality,
                ToDecimal(LowerLimit),
                ToDecimal(UpperLimit))
                          .Value;

        private static decimal ToDecimal(object? value) =>
            value is null or DBNull
                ? 0m
                : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        public static DataRow FromDomain(FdcCollectData d) => new()
        {
            CollectId   = d.Id,
            EquipmentId = d.EquipmentId,
            ParameterId = d.ParameterId,
            Value       = d.Value,
            CollectedAt = d.CollectedAt,
            Quality     = d.Quality,
            LowerLimit  = d.LowerLimit,
            UpperLimit  = d.UpperLimit
        };
    }

    private sealed class RetentionStateRow
    {
        public DateTime? CompletenessBoundary { get; set; }
    }
}
