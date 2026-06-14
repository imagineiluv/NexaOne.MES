using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class UserRepository : QueryRepository, IUserRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public UserRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<User?> GetByIdAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_USER WHERE USER_ID = @userId";
        var row = await QueryFirstOrDefaultAsync<UserRow>(sql, new { userId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<User>> GetAllActiveAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_USER WHERE IS_ACTIVE = 1 AND IS_DELETED = 0";
        var rows = await QueryAsync<UserRow>(sql, null, ct);
        return rows.Select(r => r.ToDomain()).OfType<User>().ToList();
    }

    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM SYS_USER WHERE USER_ID = @userId";
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
        // MSSQL 전용 OUTPUT 절을 제거해 MSSQL/SQLite 양쪽에서 동작하게 한다. 원자적 단일 UPDATE로
        // 카운트/잠금을 갱신한 뒤, 결과 LOCKED_UNTIL은 별도 SELECT로 읽는다(증가 자체는 여전히 원자적).
        const string update = @"UPDATE SYS_USER SET
            FAIL_COUNT = CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @Now
                              THEN 1 ELSE FAIL_COUNT + 1 END,
            LOCKED_UNTIL = CASE
                WHEN (CASE WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @Now
                           THEN 1 ELSE FAIL_COUNT + 1 END) >= @MaxFailures THEN @LockUntil
                WHEN LOCKED_UNTIL IS NOT NULL AND LOCKED_UNTIL <= @Now THEN NULL
                ELSE LOCKED_UNTIL END,
            UPDATED_BY = 'SYSTEM', UPDATED_AT = @Now
            WHERE USER_ID = @UserId";
        await _processor.UpdateAsync(update, new
        {
            UserId = userId,
            Now = utcNow,
            MaxFailures = User.MaxConsecutiveFailures,
            LockUntil = utcNow.Add(User.LockDuration)
        }, ct);

        const string read = "SELECT LOCKED_UNTIL FROM SYS_USER WHERE USER_ID = @userId";
        return await QueryFirstOrDefaultAsync<DateTime?>(read, new { userId }, ct);
    }

    // PASSWORD_HASH 포함 — 비밀번호 변경/임시 비밀번호 발급이 영속되어야 한다 (§20.10)
    private const string UpdateSql = @"UPDATE SYS_USER SET
            USER_NAME = @UserName, PASSWORD_HASH = @PasswordHash, EMAIL = @Email,
            ROLE_ID = @RoleId, LANGUAGE = @Language,
            IS_ACTIVE = @IsActive, IS_DELETED = @IsDeleted, DELETED_AT = @DeletedAt,
            LAST_LOGIN_AT = @LastLoginAt,
            PASSWORD_STATE = @PasswordState, FAIL_COUNT = @FailCount, LOCKED_UNTIL = @LockedUntil,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE USER_ID = @UserId";

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, UserRow.FromDomain(user), ct);
            return;
        }
        // ADR-002 활성: 사용자 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(user, ct);
    }

    // 사용자 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 사용자 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(User user, CancellationToken ct)
    {
        var auditUser = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(user, auditUser, now)),
        };
        statements.AddRange(OutboxStatements.For(user.DomainEvents.OfType<IOutboxEvent>(), auditUser, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        user.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(User user, string auditUser, DateTime now)
    {
        var p = new Dapper.DynamicParameters(UserRow.FromDomain(user));
        p.Add("UpdatedBy", auditUser);
        p.Add("UpdatedAt", now);
        return p;
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
            if (!Enum.TryParse<LanguageType>(Language, out var lang) || !Enum.IsDefined(lang)) lang = LanguageType.KoKr;
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
