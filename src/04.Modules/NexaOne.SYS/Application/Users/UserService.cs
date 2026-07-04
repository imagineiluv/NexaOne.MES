using NexaOne.SYS.Domain;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.SYS.Application.Users;

public sealed class UserService
{
    private readonly IUserRepository _userRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly IMultiLanguageResourceRepository _langRepository;
    private readonly ILoginFailureHistoryRepository _failureRepository;

    public UserService(
        IUserRepository userRepository,
        IRoleRepository roleRepository,
        IMultiLanguageResourceRepository langRepository,
        ILoginFailureHistoryRepository failureRepository)
    {
        _userRepository = userRepository;
        _roleRepository = roleRepository;
        _langRepository = langRepository;
        _failureRepository = failureRepository;
    }

    // ── User ──────────────────────────────────────────────────────────────────

    public async Task<Result<User>> CreateUserAsync(
        string userId,
        string userName,
        string passwordHash,
        string email,
        string roleId,
        LanguageType language = LanguageType.KoKr,
        CancellationToken ct = default)
    {
        if (await _userRepository.ExistsAsync(userId, ct))
            return Result.Failure<User>(Error.Conflict($"User '{userId}' already exists."));

        var result = User.Create(userId, userName, passwordHash, email, roleId, language);
        if (result.IsFailure) return result;

        await _userRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result<IReadOnlyList<User>>> GetAllUsersAsync(CancellationToken ct = default)
    {
        var users = await _userRepository.GetAllActiveAsync(ct);
        return Result.Success(users);
    }

    public async Task<Result<User>> GetUserAsync(string userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        return user is null
            ? Result.Failure<User>(Error.NotFound(nameof(User), $"User '{userId}'을(를) 찾을 수 없습니다."))
            : Result.Success(user);
    }

    /// <summary>§20.10 — 자격 검증 + 잠금 처리. 5회 연속 실패 시 30분 잠금,
    /// 모든 실패는 SYS_LOGIN_FAILURE_HIST에 사용자/IP/UserAgent/사유와 함께 기록한다.</summary>
    public async Task<Result<User>> ValidateAndLoginAsync(
        string userId,
        string password,
        string ipAddress = "",
        string userAgent = "",
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = await _userRepository.GetByIdAsync(userId, ct);

        if (user is null)
        {
            await RecordFailureAsync(userId, ipAddress, userAgent, LoginFailureHistory.Reasons.UserNotFound, now, ct);
            return Result.Failure<User>(InvalidCredentials());
        }

        if (user.IsLockedAt(now))
        {
            await RecordFailureAsync(userId, ipAddress, userAgent, LoginFailureHistory.Reasons.AccountLocked, now, ct);
            return Result.Failure<User>(AccountLocked(user.LockedUntil!.Value, now));
        }

        if (!user.IsActive || user.IsDeleted)
        {
            await RecordFailureAsync(userId, ipAddress, userAgent, LoginFailureHistory.Reasons.InactiveUser, now, ct);
            return Result.Failure<User>(InvalidCredentials());
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            // §20.10 — 카운터 증가는 원자 UPDATE로 처리한다. 메모리 증가 후 전체 행 UPDATE는
            // 동시 실패 시 증가가 유실되고, 동시 임시 비밀번호 발급을 덮어쓸 수 있다.
            var lockedUntil = await _userRepository.RecordLoginFailureAsync(userId, now, ct);
            await RecordFailureAsync(userId, ipAddress, userAgent, LoginFailureHistory.Reasons.WrongPassword, now, ct);
            return lockedUntil is { } until && until > now
                ? Result.Failure<User>(AccountLocked(until, now))
                : Result.Failure<User>(InvalidCredentials());
        }

        // §19.2.2 rehash-on-login — 구 무염 SHA-256 해시는 검증 성공 시 강화 해시로 교체(상태는 보존)
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
            user.RehashPassword(PasswordHasher.Hash(password));

        user.RecordLogin();   // LastLoginAt 기록 + 연속 실패 카운터 리셋
        await _userRepository.UpdateAsync(user, ct);
        return Result.Success(user);
    }

    /// <summary>ADR-003 — 역할의 유효 권한(명시 권한 ∪ 역할명 기본 매핑). 토큰 발급 시 permission 클레임으로 싣는다.</summary>
    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(string roleId, CancellationToken ct = default)
    {
        var perms = new HashSet<string>(RolePermissionDefaults.For(roleId), StringComparer.OrdinalIgnoreCase);
        var role = await _roleRepository.GetByIdAsync(roleId, ct);
        if (role is not null)
            foreach (var p in role.Permissions) perms.Add(p);
        return perms.ToList();
    }

    /// <summary>§20.10 — 관리자 잠금 해제. 해제는 시간 만료 또는 관리자만 가능하다.</summary>
    public async Task<Result> UnlockUserAsync(string userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound(nameof(User), $"User '{userId}'을(를) 찾을 수 없습니다."));

        user.Unlock();
        await _userRepository.UpdateAsync(user, ct);
        return Result.Success();
    }

    private static Error InvalidCredentials()
        => Error.Failure("Auth.InvalidCredentials", "Invalid credentials.");

    private static Error AccountLocked(DateTime lockedUntil, DateTime now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
        return Error.Failure("Auth.AccountLocked",
            $"비밀번호 5회 연속 오류로 계정이 잠겼습니다. 약 {minutes}분 후 다시 시도하거나 관리자에게 문의하세요.");
    }

    private async Task RecordFailureAsync(
        string userId, string ipAddress, string userAgent, string reason, DateTime occurredAt, CancellationToken ct)
        => await _failureRepository.AddAsync(
            LoginFailureHistory.Create(userId, ipAddress, userAgent, reason, occurredAt), ct);

    public async Task<Result> ChangePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound(nameof(User), $"User '{userId}'을(를) 찾을 수 없습니다."));

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
            return Result.Failure(Error.Failure("Auth.WrongPassword", "Current password is incorrect."));

        user.ChangePassword(PasswordHasher.Hash(newPassword));
        await _userRepository.UpdateAsync(user, ct);
        return Result.Success();
    }

    /// <summary>§20.10 — 임시 비밀번호 발급. 비밀번호 상태를 Forgot으로 표시해
    /// 로그인 후 변경 완료 전까지 업무 API를 차단하게 한다.</summary>
    public async Task<Result> ForceSetPasswordAsync(
        string userId,
        string newPasswordHash,
        CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound(nameof(User), $"User '{userId}'을(를) 찾을 수 없습니다."));

        user.SetTemporaryPassword(newPasswordHash);
        await _userRepository.UpdateAsync(user, ct);
        return Result.Success();
    }

    public async Task<Result> DeactivateUserAsync(string userId, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound(nameof(User), $"User '{userId}'을(를) 찾을 수 없습니다."));

        user.Deactivate();
        await _userRepository.UpdateAsync(user, ct);
        return Result.Success();
    }

    // ── Role ──────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<Role>>> GetAllRolesAsync(CancellationToken ct = default)
    {
        var roles = await _roleRepository.GetAllAsync(ct);
        return Result.Success(roles);
    }

    public async Task<Result<Role>> GetRoleAsync(string roleId, CancellationToken ct = default)
    {
        var role = await _roleRepository.GetByIdAsync(roleId, ct);
        return role is null
            ? Result.Failure<Role>(Error.NotFound(nameof(Role), $"Role '{roleId}'을(를) 찾을 수 없습니다."))
            : Result.Success(role);
    }

    public async Task<Result<Role>> CreateRoleAsync(
        string roleId,
        string roleName,
        string description = "",
        CancellationToken ct = default)
    {
        if (await _roleRepository.ExistsAsync(roleId, ct))
            return Result.Failure<Role>(Error.Conflict($"Role '{roleId}' already exists."));

        var role = Role.Create(roleId, roleName, description);
        await _roleRepository.AddAsync(role, ct);
        return Result.Success(role);
    }

    public async Task<Result> AddPermissionAsync(
        string roleId,
        string permission,
        CancellationToken ct = default)
    {
        var role = await _roleRepository.GetByIdAsync(roleId, ct);
        if (role is null)
            return Result.Failure(Error.NotFound(nameof(Role), $"Role '{roleId}'을(를) 찾을 수 없습니다."));

        role.AddPermission(permission);
        await _roleRepository.UpdateAsync(role, ct);
        return Result.Success();
    }

    public async Task<Result> RemovePermissionAsync(
        string roleId,
        string permission,
        CancellationToken ct = default)
    {
        var role = await _roleRepository.GetByIdAsync(roleId, ct);
        if (role is null)
            return Result.Failure(Error.NotFound(nameof(Role), $"Role '{roleId}'을(를) 찾을 수 없습니다."));

        role.RemovePermission(permission);
        await _roleRepository.UpdateAsync(role, ct);
        return Result.Success();
    }

    // ── MultiLanguageResource ─────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<MultiLanguageResource>>> GetResourcesByMenuAsync(
        string menuId,
        CancellationToken ct = default)
    {
        var resources = await _langRepository.GetByMenuIdAsync(menuId, ct);
        return Result.Success(resources);
    }

    public async Task<Result<IReadOnlyList<MultiLanguageResource>>> GetResourcesByLanguageAsync(
        LanguageType language,
        CancellationToken ct = default)
    {
        var resources = await _langRepository.GetByLanguageAsync(language, ct);
        return Result.Success(resources);
    }

    public async Task<Result<MultiLanguageResource>> UpsertResourceAsync(
        string resourceKey,
        string menuId,
        LanguageType language,
        string value,
        CancellationToken ct = default)
    {
        var resource = MultiLanguageResource.Create(resourceKey, menuId, language, value);

        if (await _langRepository.ExistsAsync(resourceKey, language, ct))
        {
            resource.UpdateValue(value);
            await _langRepository.UpdateAsync(resource, ct);
        }
        else
        {
            await _langRepository.AddAsync(resource, ct);
        }

        return Result.Success(resource);
    }
}
