using NexaOne.Common;

namespace NexaOne.FDC.Domain;

/// <summary>임계치 기반 FDC 알람 설정 (FDC_ALARM_CONFIG, design 10.4.1).
/// 인터락(정지)과 달리 경고 레벨(Warning/Critical) 알림만 발생시킨다.</summary>
public sealed class FdcAlarmConfig : AuditableEntity<string>
{
    private static readonly HashSet<string> Levels = new(StringComparer.OrdinalIgnoreCase) { "Warning", "Critical" };
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "GT", "LT", "GTE", "LTE", "EQ" };

    private FdcAlarmConfig(string alarmConfigId) : base(alarmConfigId) { }

    public string EquipmentId { get; private set; } = string.Empty;
    public string ParameterId { get; private set; } = string.Empty;
    public string AlarmLevel { get; private set; } = string.Empty;
    public string Operator { get; private set; } = string.Empty;
    public decimal ThresholdValue { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<FdcAlarmConfig> Create(
        string alarmConfigId,
        string equipmentId,
        string parameterId,
        string alarmLevel,
        string @operator,
        decimal thresholdValue)
    {
        if (string.IsNullOrWhiteSpace(alarmConfigId))
            return Result.Failure<FdcAlarmConfig>(Error.Validation(nameof(alarmConfigId), "Alarm config ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcAlarmConfig>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(parameterId))
            return Result.Failure<FdcAlarmConfig>(Error.Validation(nameof(parameterId), "Parameter ID is required."));
        if (!Levels.Contains(alarmLevel))
            return Result.Failure<FdcAlarmConfig>(Error.Validation(nameof(alarmLevel), "Alarm level must be Warning or Critical."));
        if (!Operators.Contains(@operator))
            return Result.Failure<FdcAlarmConfig>(Error.Validation(nameof(@operator), "Operator must be GT, LT, GTE, LTE, or EQ."));

        // 입력 레벨의 표준 표기(첫 글자 대문자)로 보관
        var normalizedLevel = string.Equals(alarmLevel, "Critical", StringComparison.OrdinalIgnoreCase) ? "Critical" : "Warning";

        var config = new FdcAlarmConfig(alarmConfigId)
        {
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            AlarmLevel = normalizedLevel,
            Operator = @operator,
            ThresholdValue = thresholdValue,
            IsActive = true
        };
        return config;
    }

    public bool Evaluate(decimal value) => Operator switch
    {
        "GT"  => value > ThresholdValue,
        "LT"  => value < ThresholdValue,
        "GTE" => value >= ThresholdValue,
        "LTE" => value <= ThresholdValue,
        "EQ"  => value == ThresholdValue,
        _     => false
    };

    public void Deactivate() => IsActive = false;
}
