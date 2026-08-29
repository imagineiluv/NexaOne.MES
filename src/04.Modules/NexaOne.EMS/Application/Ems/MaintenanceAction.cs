using NexaOne.Common;

namespace NexaOne.EMS.Application.Ems;

/// <summary>
/// HTTP/장비 어댑터가 전달하는 보전 명령 문맥. ActorId는 인증 경계에서 만들고 body 값으로 받지 않는다.
/// </summary>
public sealed record MaintenanceCommandContext(
    string ActorId,
    string IdempotencyKey,
    string ClientChannel,
    string? DeviceId = null,
    string? CorrelationId = null,
    string Source = "Manual")
{
    private static readonly HashSet<string> Channels =
        new(StringComparer.OrdinalIgnoreCase) { "MES", "MOBILE", "POP" };

    public static Result<MaintenanceCommandContext> Create(
        string? actorId,
        string? idempotencyKey,
        string? clientChannel,
        string? deviceId = null,
        string? correlationId = null,
        string? source = null)
    {
        var actor = actorId?.Trim() ?? string.Empty;
        var key = idempotencyKey?.Trim() ?? string.Empty;
        var channel = string.IsNullOrWhiteSpace(clientChannel)
            ? "MES"
            : clientChannel.Trim().ToUpperInvariant();
        var normalizedDevice = Trimmed(deviceId);
        var normalizedCorrelation = Trimmed(correlationId);
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "Manual" : source.Trim();

        if (actor.Length == 0)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(actorId), "Authenticated maintenance actor is required."));
        if (actor.Length > 50)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(actorId), "Maintenance actor cannot exceed 50 characters."));
        if (key.Length == 0)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(idempotencyKey), "Idempotency key is required."));
        if (key.Length > 150)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(idempotencyKey), "Idempotency key cannot exceed 150 characters."));
        if (!Channels.Contains(channel))
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(clientChannel), "Client channel must be MES, MOBILE, or POP."));
        if (normalizedDevice?.Length > 100)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(deviceId), "Device ID cannot exceed 100 characters."));
        if (normalizedCorrelation?.Length > 100)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(correlationId), "Correlation ID cannot exceed 100 characters."));
        if (normalizedSource.Length > 20)
            return Result.Failure<MaintenanceCommandContext>(
                Error.Validation(nameof(source), "Maintenance source cannot exceed 20 characters."));

        return Result.Success(new MaintenanceCommandContext(
            actor, key, channel, normalizedDevice, normalizedCorrelation, normalizedSource));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 보전 작업지시의 한 번의 사용자 행동. 담당 예정자와 실제 실행자를 분리하고, 행은 생성 후 수정하지 않는다.
/// </summary>
public sealed record MaintenanceAction(
    string ActionId,
    string WorkOrderId,
    string ActionType,
    string? FromStatus,
    string ToStatus,
    string ActorId,
    string IdempotencyKey,
    DateTime ActionAt,
    string Source = "Manual",
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null,
    string? Remark = null);

/// <summary>
/// 보전계획 상태 변경과 같은 트랜잭션에 append되는 인증 행동 증거다. 전역 멱등 원장을
/// 작업지시와 공유하므로 PlanId는 nullable이며, 다른 EMS 명령이 선점한 키도 충돌로 판별한다.
/// </summary>
public sealed record MaintenancePlanAction(
    string ActionId,
    string? PlanId,
    string ActionType,
    string? FromStatus,
    string ToStatus,
    string ActorId,
    string IdempotencyKey,
    DateTime ActionAt,
    string Source = "Manual",
    string ClientChannel = "MES",
    string? DeviceId = null,
    string? CorrelationId = null,
    string? WorkOrderId = null);
