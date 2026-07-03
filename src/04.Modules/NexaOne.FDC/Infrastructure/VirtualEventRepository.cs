using NexaOne.FDC.Application.Fdc;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.FDC.Infrastructure;

/// <summary>가상 이벤트 평가 포트 구현 — FDC_VIRTUAL_EVENT(정의)/FDC_COLLECT_DATA(최신값)/
/// FDC_VIRTUAL_EVENT_HISTORY(전이 이력, V069). 최신값은 파라미터별 MAX(COLLECTED_AT) 조인(방언 무관 SQL),
/// 직전 상태만 페이징이 필요해 dialect(WrapPaged)를 쓴다(FdcCollectDataRepository 관례).</summary>
public sealed class VirtualEventRepository : QueryRepository, IVirtualEventRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public VirtualEventRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<VirtualEventDefinition?> GetDefinitionAsync(
        string equipmentId, string eventId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT PLANT_ID, EQUIPMENT_ID, EVENT_ID, EVENT_NAME, CONDITION_FORMULA
            FROM FDC_VIRTUAL_EVENT
            WHERE EQUIPMENT_ID = @equipmentId AND EVENT_ID = @eventId AND VALID_STATE = 'Valid'";
        var rows = await QueryAsync<DefinitionRow>(sql, new { equipmentId, eventId }, ct);
        return rows.Select(ToDefinition).FirstOrDefault();
    }

    public async Task<IReadOnlyList<VirtualEventDefinition>> GetActiveDefinitionsAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT PLANT_ID, EQUIPMENT_ID, EVENT_ID, EVENT_NAME, CONDITION_FORMULA
            FROM FDC_VIRTUAL_EVENT
            WHERE VALID_STATE = 'Valid'
            ORDER BY EQUIPMENT_ID, EVENT_ID";
        var rows = await QueryAsync<DefinitionRow>(sql, new { }, ct);
        return rows.Select(ToDefinition).ToList();
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestParameterValuesAsync(
        string equipmentId, CancellationToken ct = default)
    {
        // 파라미터별 최신 1건 — MAX(COLLECTED_AT) 조인(방언 무관). 동시각 중복 행은 first-wins로 흡수한다.
        const string sql = @"
            SELECT A.PARAMETER_ID, A.VALUE
            FROM FDC_COLLECT_DATA A
            INNER JOIN (
                SELECT PARAMETER_ID, MAX(COLLECTED_AT) AS MAX_AT
                FROM FDC_COLLECT_DATA
                WHERE EQUIPMENT_ID = @equipmentId
                GROUP BY PARAMETER_ID
            ) M ON M.PARAMETER_ID = A.PARAMETER_ID AND M.MAX_AT = A.COLLECTED_AT
            WHERE A.EQUIPMENT_ID = @equipmentId";
        var rows = await QueryAsync<ValueRow>(sql, new { equipmentId }, ct);
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (row.PARAMETER_ID is { Length: > 0 } && row.VALUE is not null && !map.ContainsKey(row.PARAMETER_ID))
                map[row.PARAMETER_ID] = Convert.ToDecimal(row.VALUE, System.Globalization.CultureInfo.InvariantCulture);
        return map;
    }

    public async Task<string?> GetLastEventStateAsync(string equipmentId, string eventId, CancellationToken ct = default)
    {
        var sql = _dialect.WrapPaged(
            @"SELECT EVENT_STATE FROM FDC_VIRTUAL_EVENT_HISTORY
              WHERE EQUIPMENT_ID = @equipmentId AND EVENT_ID = @eventId",
            "EVALUATED_AT DESC", 0, 1);
        var rows = await QueryAsync<StateRow>(sql, new { equipmentId, eventId }, ct);
        return rows.FirstOrDefault()?.EVENT_STATE;
    }

    public Task InsertHistoryAsync(
        string equipmentId, string eventId, string eventState, string? formula, string? details,
        DateTime evaluatedAt, CancellationToken ct = default)
    {
        // V069는 감사 컬럼 없는 수집 시계열 — raw 실행(ExecuteManyAsync 단건, 감사 주입 불필요).
        const string sql = @"
            INSERT INTO FDC_VIRTUAL_EVENT_HISTORY
                (HISTORY_ID, EQUIPMENT_ID, EVENT_ID, EVENT_STATE, FORMULA, DETAILS, EVALUATED_AT)
            VALUES (@historyId, @equipmentId, @eventId, @eventState, @formula, @details, @evaluatedAt)";
        return _processor.ExecuteManyAsync(ct, (sql, new
        {
            historyId = Guid.NewGuid().ToString("N"),
            equipmentId,
            eventId,
            eventState,
            formula,
            details,
            evaluatedAt,
        }));
    }

    private static VirtualEventDefinition ToDefinition(DefinitionRow r)
        => new(r.PLANT_ID ?? "", r.EQUIPMENT_ID ?? "", r.EVENT_ID ?? "", r.EVENT_NAME ?? "", r.CONDITION_FORMULA);

    private sealed class DefinitionRow
    {
        public string? PLANT_ID { get; set; }
        public string? EQUIPMENT_ID { get; set; }
        public string? EVENT_ID { get; set; }
        public string? EVENT_NAME { get; set; }
        public string? CONDITION_FORMULA { get; set; }
    }

    private sealed class ValueRow
    {
        public string? PARAMETER_ID { get; set; }
        // SQLite는 정수값을 INTEGER(Int64)로 돌려줘 decimal 직접 매핑이 깨진다 — object 수신 후 Convert.
        public object? VALUE { get; set; }
    }

    private sealed class StateRow
    {
        public string? EVENT_STATE { get; set; }
    }
}
