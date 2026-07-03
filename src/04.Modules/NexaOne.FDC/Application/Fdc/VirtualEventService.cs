using NexaOne.Common;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>가상 이벤트 평가 엔진(V067 정의 → V069 전이 이력). 평가 흐름:
/// 정의 로드 → 설비 파라미터 최신 수집값 로드 → CONDITION_FORMULA 평가(On/Off) → 직전 상태와 다를 때만
/// 이력 1행 기록(전이 기록 — 동일 상태 반복 평가는 미기록). 수식/값 부재는 실패로 보고한다(조용한 false 금지).
/// 레거시 EVENT_ON/EVENT_OFF 별도 판정값은 v1에서 미사용 — CONDITION_FORMULA 참=On, 거짓=Off 단일 규약.</summary>
public sealed class VirtualEventService
{
    public const string StateOn = "On";
    public const string StateOff = "Off";

    private readonly IVirtualEventRepository _repository;

    public VirtualEventService(IVirtualEventRepository repository) => _repository = repository;

    public async Task<Result<VirtualEventEvaluation>> EvaluateAsync(
        string equipmentId, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(equipmentId) || string.IsNullOrWhiteSpace(eventId))
            return Result.Failure<VirtualEventEvaluation>(
                Error.Validation("VirtualEvent.KeyRequired", "equipmentId/eventId는 필수입니다."));

        var definition = await _repository.GetDefinitionAsync(equipmentId.Trim(), eventId.Trim(), ct);
        if (definition is null)
            return Result.Failure<VirtualEventEvaluation>(
                Error.NotFound("VirtualEvent", $"{equipmentId}/{eventId}"));

        return await EvaluateDefinitionAsync(definition, ct);
    }

    /// <summary>워커 주기 평가 — 유효 정의 전체를 평가한다. 개별 실패는 결과에 담고 계속 진행(전체 중단 금지).</summary>
    public async Task<IReadOnlyList<Result<VirtualEventEvaluation>>> EvaluateAllAsync(CancellationToken ct = default)
    {
        var results = new List<Result<VirtualEventEvaluation>>();
        foreach (var definition in await _repository.GetActiveDefinitionsAsync(ct))
            results.Add(await EvaluateDefinitionAsync(definition, ct));
        return results;
    }

    private async Task<Result<VirtualEventEvaluation>> EvaluateDefinitionAsync(
        VirtualEventDefinition definition, CancellationToken ct)
    {
        var values = await _repository.GetLatestParameterValuesAsync(definition.EquipmentId, ct);
        var evaluated = VirtualEventFormula.Evaluate(definition.ConditionFormula, values);
        if (evaluated.IsFailure)
            return Result.Failure<VirtualEventEvaluation>(evaluated.Error);

        var state = evaluated.Value ? StateOn : StateOff;
        var lastState = await _repository.GetLastEventStateAsync(definition.EquipmentId, definition.EventId, ct);
        var changed = !string.Equals(lastState, state, StringComparison.OrdinalIgnoreCase);
        var evaluatedAt = DateTime.UtcNow;

        if (changed)
        {
            // 전이만 기록 — DETAILS에 평가에 쓰인 최신값 요약(진단용, 1000자 상한은 DB 스키마).
            var details = string.Join(", ", values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .Select(v => $"{v.Key}={v.Value}"));
            await _repository.InsertHistoryAsync(
                definition.EquipmentId, definition.EventId, state, definition.ConditionFormula,
                details.Length > 1000 ? details[..1000] : details, evaluatedAt, ct);
        }

        return Result.Success(new VirtualEventEvaluation(
            definition.EquipmentId, definition.EventId, definition.EventName,
            evaluated.Value, changed, evaluatedAt));
    }
}

/// <summary>평가 결과 — Changed=직전 기록 상태와 달라 이력이 기록됐는지(첫 평가 포함).</summary>
public sealed record VirtualEventEvaluation(
    string EquipmentId, string EventId, string EventName, bool IsOn, bool Changed, DateTime EvaluatedAt);
