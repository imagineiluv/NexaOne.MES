using NexaOne.Common;
using NexaOne.FDC.Domain;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>알람 평가 결과 — 발동한 설정(레벨·메시지)을 담는다.</summary>
public sealed record AlarmResult(string AlarmConfigId, string AlarmLevel, string Message);

/// <summary>FDC 임계치 알람 서비스 — 수집 값에 대해 활성 알람 설정을 평가하고
/// 발생/해제 이력(FDC_ALARM_HISTORY)을 관리한다 (design 10.4.1).</summary>
public sealed class FdcAlarmService
{
    private readonly IFdcAlarmConfigRepository _configRepository;
    private readonly IFdcAlarmHistoryRepository? _historyRepository;

    public FdcAlarmService(
        IFdcAlarmConfigRepository configRepository,
        IFdcAlarmHistoryRepository? historyRepository = null)
    {
        _configRepository = configRepository;
        _historyRepository = historyRepository;
    }

    public async Task<IReadOnlyList<FdcAlarmConfig>> GetConfigsAsync(string equipmentId, CancellationToken ct = default)
        => await _configRepository.GetByEquipmentAsync(equipmentId, ct);

    /// <summary>설비의 알람 발생 이력을 기간으로 조회한다(이력 리포지토리 미구성 시 빈 목록).</summary>
    public async Task<IReadOnlyList<FdcAlarmHistory>> GetHistoryAsync(
        string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
        => _historyRepository is null
            ? Array.Empty<FdcAlarmHistory>()
            : await _historyRepository.GetByEquipmentAsync(equipmentId, from, to, ct);

    public async Task<Result<FdcAlarmConfig>> CreateConfigAsync(
        string alarmConfigId, string equipmentId, string parameterId,
        string alarmLevel, string @operator, decimal threshold, CancellationToken ct = default)
    {
        var result = FdcAlarmConfig.Create(alarmConfigId, equipmentId, parameterId, alarmLevel, @operator, threshold);
        if (result.IsFailure) return result;
        await _configRepository.AddAsync(result.Value, ct);
        return result;
    }

    /// <summary>값에 대해 활성 알람 설정을 모두 평가한다 — 발동한 설정 목록을 반환(복수 레벨 가능).</summary>
    public async Task<IReadOnlyList<AlarmResult>> EvaluateAsync(
        string equipmentId, string parameterId, decimal value, CancellationToken ct = default)
    {
        var configs = await _configRepository.GetActiveConfigsAsync(equipmentId, parameterId, ct);
        var hits = new List<AlarmResult>();
        foreach (var cfg in configs)
        {
            if (cfg.Evaluate(value))
                hits.Add(new AlarmResult(cfg.Id, cfg.AlarmLevel,
                    $"[{cfg.AlarmLevel}] {parameterId} value {value} {cfg.Operator} {cfg.ThresholdValue}."));
        }
        return hits;
    }

    /// <summary>발동한 알람을 FDC_ALARM_HISTORY에 1행 기록한다(이력 리포지토리 미구성 시 no-op).</summary>
    public async Task<Result<FdcAlarmHistory>> RecordAsync(
        string equipmentId, string parameterId, decimal value, AlarmResult alarm, CancellationToken ct = default)
    {
        if (_historyRepository is null)
            return Result.Failure<FdcAlarmHistory>(Error.Validation(nameof(_historyRepository), "History repository is not configured."));

        var history = FdcAlarmHistory.Create(
            Guid.NewGuid().ToString("N"), alarm.AlarmConfigId, equipmentId, parameterId,
            alarm.AlarmLevel, value, alarm.Message, DateTime.UtcNow);
        if (history.IsFailure) return history;

        await _historyRepository.AddAsync(history.Value, ct);
        return history;
    }

    /// <summary>해당 설비·파라미터의 미해제(open) 알람을 모두 해제한다 — 값이 정상으로 복귀했을 때.
    /// 해제한 건수를 반환(이력 리포지토리 미구성 시 0).</summary>
    public async Task<int> ClearActiveAsync(string equipmentId, string parameterId, CancellationToken ct = default)
    {
        if (_historyRepository is null) return 0;

        var open = await _historyRepository.GetOpenAsync(equipmentId, ct);
        var targets = open.Where(h => h.ParameterId == parameterId).ToList();
        foreach (var history in targets)
        {
            history.Clear(DateTime.UtcNow);
            await _historyRepository.UpdateAsync(history, ct);
        }
        return targets.Count;
    }
}
