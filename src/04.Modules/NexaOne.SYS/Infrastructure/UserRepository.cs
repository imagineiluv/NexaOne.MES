using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class UserRepository : QueryRepository, IUserRepository
{
    private readonly ServiceObjectProcessor _processor;

    public UserRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<User?> GetByIdAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_USER WITH(NOLOCK) WHERE USER_ID = @userId";
        var row = await QueryFirstOrDefaultAsync<UserRow>(sql, new { userId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<User>> GetAllActiveAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_USER WITH(NOLOCK) WHERE IS_ACTIVE = 1 AND IS_DELETED = 0";
        var rows = await QueryAsync<UserRow>(sql, null, ct);
        return rows.Select(r => r.ToDomain()).OfType<User>().ToList();
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM SYS_USER WITH(NOLOCK) WHERE USER_ID = @userId";
        return await CountAsync(sql, new { userId }, ct) > 0;
    }

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SYS_USER
            (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE, IS_ACTIVE,
             PASSWORD_STATE, FAIL_COUNT, LOCKED_UNTIL,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@UserId, @UserName, @PasswordHash, @Email, @RoleId, @Language, @IsActive,
             @PasswordState, @FailCount, @LockedUntil,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, UserRow.FromDomain(user), ct);
    }

    public async Task<DateTime?> RecordLoginFailureAsync(
        string userId, DateTime utcNow, CancellationToken ct = default)
    {
        // §20.10 — User.RecordLoginFailure의 의미론을 단일 원자 UPDATE로 구현한다 (동기화 유지할 것):
        // 잠금이 시간 만료된 뒤의 실패는 1회부터 다시 세고, 임계 도달 시 잠금을 설정한다.
        // SELECT 후 전체 행 UPDATE 방식은 동시 실패 시 증가가 유실되고
        // PASSWORD_HASH 등 다른 컬럼을 낡은 값으로 덮어쓸 수 있어 사용하지 않는다.
        const string sql = @"UPDATE SYS_USER SET
            FAIL_COUNT = CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @Now
                              THEN 1 ELSE FAIL_COUNT + 1 END,
            LOCKED_UNTIL = CASE
                WHEN (CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @Now
                           THEN 1 ELSE FAIL_COUNT + 1 END) >= @MaxFailures THEN @LockUntil
                WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @Now THEN NULL
                ELSE LOCKED_UNTIL END,
            UPDATED_BY = 'SYSTEM', UPDATED_AT = @Now
            OUTPUT inserted.LOCKED_UNTIL
            WHERE USER_ID = @UserId";
        return await QueryFirstOrDefaultAsync<DateTime?>(sql, new
        {
            UserId = userId,
            Now = utcNow,
            MaxFailures = User.MaxConsecutiveFailures,
            LockUntil = utcNow.Add(User.LockDuration)
        }, ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        // PASSWORD_HASH 포함 — 비밀번호 변경/임시 비밀번호 발급이 영속되어야 한다 (§20.10)
        const string sql = @"UPDATE SYS_USER SET
            USER_NAME = @UserName, PASSWORD_HASH = @PasswordHash, EMAIL = @Email,
            ROLE_ID = @RoleId, LANGUAGE = @Language,
            IS_ACTIVE = @IsActive, IS_DELETED = @IsDeleted, DELETED_AT = @DeletedAt,
            LAST_LOGIN_AT = @LastLoginAt,
            PASSWORD_STATE = @PasswordState, FAIL_COUNT = @FailCount, LOCKED_UNTIL = @LockedUntil,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE USER_ID = @UserId";
        await _processor.UpdateAsync(sql, UserRow.FromDomain(user), ct);
    }

    private sealed class UserRow
    {
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Email { get; set; } = "";
        public string RoleId { get; set; } = "";
        public string Language { get; set; } = "KoKr";
        public bool IsActive { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public string PasswordState { get; set; } = "Normal";
        public int FailCount { get; set; }
        public DateTime? LockedUntil { get; set; }

        public User ToDomain()
        {
            if (!Enum.TryParse<LanguageType>(Language, out var lang)) lang = LanguageType.KoKr;
            if (!Enum.TryParse<PasswordState>(PasswordState, out var state)) state = Domain.PasswordState.Normal;

            // Restore로 전체 컬럼을 복원한다 — Create는 IsActive=true 고정이라
            // 비활성/삭제/잠금 상태가 유실되어 인증 우회가 발생한다 (§20.10)
            return User.Restore(
                UserId, UserName, PasswordHash, Email, RoleId, lang,
                IsActive, IsDeleted, DeletedAt, LastLoginAt,
                state, FailCount, LockedUntil);
        }

        public static UserRow FromDomain(User u) => new()
        {
            UserId = u.Id,
            UserName = u.UserName,
            PasswordHash = u.PasswordHash,
            Email = u.Email,
            RoleId = u.RoleId,
            Language = u.Language.ToString(),
            IsActive = u.IsActive,
            IsDeleted = u.IsDeleted,
            DeletedAt = u.DeletedAt,
            LastLoginAt = u.LastLoginAt,
            PasswordState = u.PasswordState.ToString(),
            FailCount = u.FailCount,
            LockedUntil = u.LockedUntil
        };
    }
}
