namespace NexaOne.FDC.Application.Fdc;

/// <summary>가상 이벤트 평가 엔진의 데이터 포트(V067 정의 / V069 이력 / FDC_COLLECT_DATA 최신값).
/// 정의 CRUD는 게이트웨이 명명 쿼리 소유 — 본 포트는 평가에 필요한 읽기 + 전이 이력 기록만 가진다.</summary>
public interface IVirtualEventRepository
{
    /// <summary>유효(Valid) 정의 1건 — 없으면 null.</summary>
    Task<VirtualEventDefinition?> GetDefinitionAsync(string equipmentId, string eventId, CancellationToken ct = default);

    /// <summary>유효 정의 전체 — 워커 주기 평가용.</summary>
    Task<IReadOnlyList<VirtualEventDefinition>> GetActiveDefinitionsAsync(CancellationToken ct = default);

    /// <summary>설비의 파라미터별 최신 수집 값(PARAMETER_ID→VALUE). 수식 피연산자 해석의 단일 출처.</summary>
    Task<IReadOnlyDictionary<string, decimal>> GetLatestParameterValuesAsync(string equipmentId, CancellationToken ct = default);

    /// <summary>직전 기록된 이벤트 상태('On'/'Off') — 이력이 없으면 null(첫 평가).</summary>
    Task<string?> GetLastEventStateAsync(string equipmentId, string eventId, CancellationToken ct = default);

    Task InsertHistoryAsync(
        string equipmentId, string eventId, string eventState, string? formula, string? details,
        DateTime evaluatedAt, CancellationToken ct = default);
}

/// <summary>평가에 필요한 정의 스냅샷(V067 행 미러 — 평가 무관 컬럼 제외).</summary>
public sealed record VirtualEventDefinition(
    string PlantId, string EquipmentId, string EventId, string EventName, string? ConditionFormula);
