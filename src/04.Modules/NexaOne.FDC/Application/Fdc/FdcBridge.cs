using NexaOne.Common;
using NexaOne.FDC.Domain;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>ADR-008 얇은 브리지 어댑터 — FdcParameterGroupService(그룹)·FdcAlarmService(알람설정)·
/// FdcInterlockService(인터락규칙)의 비-실시간 설정 생성에 위임하고 도메인 엔티티를 계약 DTO로 매핑한다.
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IFdcBridge로 캐스트해 DI에 등록한다. 팩토리 검증의 Result는
/// 그대로 통과시켜 컨트롤러가 400/409/404로 매핑한다.
///
/// 워커 가드(ADR-006): FdcCollectorService(실시간 수집)·EvaluateAsync/RecordAsync/ClearActiveAsync/
/// ResolveActiveAsync/RecordDataAsync(평가·이력·수집 핫패스)는 워커가 소유하므로 본 어댑터는 위임하지 않는다.</summary>
public sealed class FdcBridge : IFdcBridge
{
    private readonly FdcParameterGroupService _groupService;
    private readonly FdcAlarmService _alarmService;
    private readonly FdcInterlockService _interlockService;
    private readonly VirtualEventService _virtualEventService;

    public FdcBridge(
        FdcParameterGroupService groupService,
        FdcAlarmService alarmService,
        FdcInterlockService interlockService,
        VirtualEventService virtualEventService)
    {
        _groupService = groupService;
        _alarmService = alarmService;
        _interlockService = interlockService;
        _virtualEventService = virtualEventService;
    }

    public async Task<Result<FdcParameterGroupDto>> CreateParameterGroupAsync(
        string groupId, string groupName, string equipmentId, string? description, int displayOrder, CancellationToken ct = default)
    {
        var r = await _groupService.CreateGroupAsync(groupId, groupName, equipmentId, description, displayOrder, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<FdcParameterGroupDto>(r.Error);
    }

    public async Task<Result<FdcAlarmConfigDto>> CreateAlarmConfigAsync(
        string alarmConfigId, string equipmentId, string parameterId, string alarmLevel, string @operator,
        decimal threshold, CancellationToken ct = default)
    {
        var r = await _alarmService.CreateConfigAsync(alarmConfigId, equipmentId, parameterId, alarmLevel, @operator, threshold, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<FdcAlarmConfigDto>(r.Error);
    }

    public async Task<Result<FdcInterlockRuleDto>> CreateInterlockRuleAsync(
        string ruleId, string ruleName, string equipmentId, string parameterId, string @operator,
        decimal threshold, string action, int priority, CancellationToken ct = default)
    {
        var r = await _interlockService.CreateRuleAsync(ruleId, ruleName, equipmentId, parameterId, @operator, threshold, action, priority, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<FdcInterlockRuleDto>(r.Error);
    }

    public async Task<Result<VirtualEventEvaluationDto>> EvaluateVirtualEventAsync(
        string equipmentId, string eventId, CancellationToken ct = default)
    {
        var r = await _virtualEventService.EvaluateAsync(equipmentId, eventId, ct);
        return r.IsSuccess
            ? Result.Success(new VirtualEventEvaluationDto(
                r.Value.EquipmentId, r.Value.EventId, r.Value.EventName, r.Value.IsOn, r.Value.Changed, r.Value.EvaluatedAt))
            : Result.Failure<VirtualEventEvaluationDto>(r.Error);
    }

    // ── 매핑 ──

    private static FdcParameterGroupDto ToDto(FdcParameterGroup g)
        => new(g.Id, g.GroupName, g.EquipmentId, g.Description, g.DisplayOrder, g.IsActive);

    private static FdcAlarmConfigDto ToDto(FdcAlarmConfig c)
        => new(c.Id, c.EquipmentId, c.ParameterId, c.AlarmLevel, c.Operator, c.ThresholdValue, c.IsActive);

    private static FdcInterlockRuleDto ToDto(FdcInterlockRule r)
        => new(r.Id, r.RuleName, r.EquipmentId, r.ParameterId, r.Operator, r.ThresholdValue, r.Action, r.Priority, r.IsActive);
}
