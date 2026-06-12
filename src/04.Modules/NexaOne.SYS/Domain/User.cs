using NexaOne.Common;

namespace NexaOne.SYS.Domain;

public sealed class User : AuditableEntity<string>
{
    /// <summary>§20.10 — 연속 실패 잠금 임계값 (5회).</summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>§20.10 — 계정 잠금 시간 (30분).</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(30);

    private User(string userId) : base(userId) { }

    public string UserName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string RoleId { get; private set; } = string.Empty;
    public LanguageType Language { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? LastLoginAt { get; private set; }

    /// <summary>§19.2.1 — 비밀번호 상태. Normal 외에는 로그인 후 변경을 강제한다 (§20.10).</summary>
    public PasswordState PasswordState { get; private set; } = PasswordState.Normal;

    /// <summary>§20.10 — 연속 로그인 실패 횟수.</summary>
    public int FailCount { get; private set; }

    /// <summary>§20.10 — 잠금 만료 시각(UTC). null이면 잠기지 않음.</summary>
    public DateTime? LockedUntil { get; private set; }

    /// <summary>비밀번호 변경 강제 대상 여부 (Forgot/Create/Expired).</summary>
    public bool RequiresPasswordChange => PasswordState != PasswordState.Normal;

    public static Result<User> Create(
        string userId,
        string userName,
        string passwordHash,
        string email,
        string roleId,
        LanguageType language = LanguageType.KoKr)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<User>(Error.Validation(nameof(userId), "User ID is required."));
        if (string.IsNullOrWhiteSpace(userName))
            return Result.Failure<User>(Error.Validation(nameof(userName), "User name is required."));

        var user = new User(userId)
        {
            UserName = userName,
            PasswordHash = passwordHash,
            Email = email,
            RoleId = roleId,
            Language = language,
            IsActive = true
        };
        return user;
    }

    /// <summary>DB 행 복원 전용 — 저장된 상태를 검증 없이 그대로 되살린다. 신규 생성은 Create를 사용한다.</summary>
    public static User Restore(
        string userId,
        string userName,
        string passwordHash,
        string email,
        string roleId,
        LanguageType language,
        bool isActive,
        bool isDeleted,
        DateTime? deletedAt,
        DateTime? lastLoginAt,
        PasswordState passwordState,
        int failCount,
        DateTime? lockedUntil)
        => new(userId)
        {
            UserName = userName,
            PasswordHash = passwordHash,
            Email = email,
            RoleId = roleId,
            Language = language,
            IsActive = isActive,
            IsDeleted = isDeleted,
            DeletedAt = deletedAt,
            LastLoginAt = lastLoginAt,
            PasswordState = passwordState,
            FailCount = failCount,
            LockedUntil = lockedUntil
        };

    /// <summary>로그인 성공 — 마지막 로그인 시각을 기록하고 연속 실패 카운터를 끊는다 (§20.10).</summary>
    public void RecordLogin()
    {
        LastLoginAt = DateTime.UtcNow;
        ResetLoginFailures();
    }

    /// <summary>§20.10 — 지정 시각 기준 잠금 여부.</summary>
    public bool IsLockedAt(DateTime utcNow) => LockedUntil is { } until && until > utcNow;

    /// <summary>§20.10 — 로그인 실패 기록. 5회 연속 실패 시 30분 잠금을 설정하고 true를 반환한다.
    /// 잠금이 시간 만료된 뒤의 실패는 새 시도 구간으로 보고 1회부터 다시 센다.
    /// 운영 경로는 동시 실패 시 증가 유실을 막기 위해 이 의미론을 원자 SQL로 구현한
    /// IUserRepository.RecordLoginFailureAsync를 사용한다 — 수정 시 양쪽을 동기화할 것.</summary>
    public bool RecordLoginFailure(DateTime utcNow)
    {
        if (LockedUntil is { } until && until <= utcNow)
        {
            LockedUntil = null;
            FailCount = 0;
        }

        FailCount++;
        if (FailCount >= MaxConsecutiveFailures)
        {
            LockedUntil = utcNow.Add(LockDuration);
            return true;
        }
        return false;
    }

    /// <summary>연속 실패 카운터와 잠금을 해제한다.</summary>
    public void ResetLoginFailures()
    {
        FailCount = 0;
        LockedUntil = null;
    }

    /// <summary>§20.10 — 관리자 잠금 해제 (해제는 시간 만료 또는 관리자만 가능).</summary>
    public void Unlock() => ResetLoginFailures();

    public void Deactivate()
    {
        IsActive = false;
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
    }

    /// <summary>본인 확인을 거친 정상 변경 — 비밀번호 상태를 Normal로 되돌린다 (§20.10).</summary>
    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        PasswordState = PasswordState.Normal;
    }

    /// <summary>§20.10 — 임시 비밀번호 발급. 상태를 Forgot으로 표시해 로그인 후 변경을 강제한다.</summary>
    public void SetTemporaryPassword(string temporaryPasswordHash)
    {
        PasswordHash = temporaryPasswordHash;
        PasswordState = PasswordState.Forgot;
    }

    /// <summary>§19.3.5 — 가입 승인 시 초기 비밀번호 발급. 상태를 Create로 표시해
    /// 최초 로그인 시 변경을 강제한다 (Forgot과 구분: 신규 생성 직후라는 의미).</summary>
    public void IssueInitialPassword(string initialPasswordHash)
    {
        PasswordHash = initialPasswordHash;
        PasswordState = PasswordState.Create;
    }

    public void ChangeRole(string roleId) => RoleId = roleId;
    public void ChangeLanguage(LanguageType language) => Language = language;
}
