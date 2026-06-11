using NexaOne.Common;

namespace NexaOne.SYS.Domain;

/// <summary>설계서 §20.10 — 로그인 실패 이력 (SYS_LOGIN_FAILURE_HIST).
/// 존재하지 않는 사용자 ID에 대한 시도도 기록하므로 SYS_USER와 FK 관계를 두지 않는다.</summary>
public sealed class LoginFailureHistory : AuditableEntity<string>
{
    private LoginFailureHistory(string failureId) : base(failureId) { }

    public string UserId { get; private set; } = string.Empty;
    public string IpAddress { get; private set; } = string.Empty;
    public string UserAgent { get; private set; } = string.Empty;
    public string FailureReason { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }

    /// <summary>실패 사유 코드 — SYS_LOGIN_FAILURE_HIST.FAILURE_REASON 값.</summary>
    public static class Reasons
    {
        public const string UserNotFound = "UserNotFound";
        public const string WrongPassword = "WrongPassword";
        public const string InactiveUser = "InactiveUser";
        public const string AccountLocked = "AccountLocked";
    }

    public static LoginFailureHistory Create(
        string userId,
        string ipAddress,
        string userAgent,
        string failureReason,
        DateTime occurredAt)
    {
        // 컬럼 길이에 맞춰 절단 — 실패 기록이 길이 초과로 유실되면 안 된다 (§20.10)
        return new LoginFailureHistory(Guid.NewGuid().ToString("N"))
        {
            UserId = Truncate(userId, 50),
            IpAddress = Truncate(ipAddress, 45),
            UserAgent = Truncate(userAgent, 500),
            FailureReason = Truncate(failureReason, 50),
            OccurredAt = occurredAt
        };
    }

    /// <summary>DB 행 복원 전용.</summary>
    public static LoginFailureHistory Restore(
        string failureId,
        string userId,
        string ipAddress,
        string userAgent,
        string failureReason,
        DateTime occurredAt)
        => new(failureId)
        {
            UserId = userId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            FailureReason = failureReason,
            OccurredAt = occurredAt
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
