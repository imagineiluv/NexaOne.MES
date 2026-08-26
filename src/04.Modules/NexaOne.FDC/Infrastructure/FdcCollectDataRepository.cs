using System.Globalization;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcCollectDataRepository : QueryRepository, IFdcCollectDataRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public FdcCollectDataRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<IReadOnlyList<FdcCollectData>> GetByParameterAsync(
        string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        // 행 상한(@limit)을 TOP/LIMIT로 SQL에 밀어 무제한 시계열 조회를 방어한다(GetLatestAsync와 동일 페이징).
        // 범위가 상한을 넘으면 최신 @limit건을 가져온 뒤 Reverse로 시간 오름차순(기존 출력 계약)을 복원한다.
        var sql = _dialect.WrapPaged(
            @"SELECT * FROM FDC_COLLECT_DATA
              WHERE PARAMETER_ID = @parameterId
                AND COLLECTED_AT >= @from
                AND COLLECTED_AT <= @to",
            "COLLECTED_AT DESC", 0, limit);
        var rows = await QueryAsync<DataRow>(sql, new { parameterId, from, to }, ct);
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
        var sql = _dialect.WrapPaged(
            @"SELECT * FROM FDC_COLLECT_DATA
              WHERE EQUIPMENT_ID = @equipmentId
                AND PARAMETER_ID = @parameterId
                AND COLLECTED_AT >= @effectiveFrom
                AND (@effectiveTo IS NULL OR COLLECTED_AT < @effectiveTo)
                AND (@afterCollectedAt IS NULL
                     OR COLLECTED_AT > @afterCollectedAt
                     OR (COLLECTED_AT = @afterCollectedAt AND COLLECT_ID > @afterCollectId))",
            "COLLECTED_AT, COLLECT_ID",
            0,
            Math.Clamp(limit, 1, 5000));
        var rows = await QueryAsync<DataRow>(sql, new
        {
            equipmentId,
            parameterId,
            effectiveFrom,
            effectiveTo,
            afterCollectedAt,
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
        await _processor.InsertAsync(InsertSql, DataRow.FromDomain(data), ct);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        // 보존정리: 기준시각 이전 시계열 행을 삭제한다. 기준시각은 호출부(C#)에서 산정해 파라미터로 넘겨
        // MSSQL/SQLite 날짜 방언 분기를 피한다. DeleteAsync는 감사 미주입 raw 실행이며 영향 행 수를 반환한다.
        const string sql = "DELETE FROM FDC_COLLECT_DATA WHERE COLLECTED_AT < @cutoff";
        return await _processor.DeleteAsync(sql, new { cutoff }, ct);
    }

    public async Task AddBatchAsync(IEnumerable<FdcCollectData> data, CancellationToken ct = default)
    {
        // N건을 단일 트랜잭션에서 일괄 INSERT한다 — 행마다 별도 트랜잭션을 여는 N회 InsertAsync(원자성·왕복 손실)
        // 대신 ExecuteManyAsync로 한 트랜잭션에 묶어, 한 건이라도 실패하면 전체가 롤백되어 부분 적재를 막는다.
        var statements = data
            .Select(item => (Sql: InsertSql, Param: (object?)DataRow.FromDomain(item)))
            .ToArray();
        if (statements.Length == 0) return;
        await _processor.ExecuteManyAsync(ct, statements);
    }

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
}
