using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.SYS.Infrastructure;

public sealed class LoginFailureHistoryRepository : QueryRepository, ILoginFailureHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public LoginFailureHistoryRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task AddAsync(LoginFailureHistory history, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SYS_LOGIN_FAILURE_HIST
            (FAILURE_ID, USER_ID, IP_ADDRESS, USER_AGENT, FAILURE_REASON, OCCURRED_AT,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@FailureId, @UserId, @IpAddress, @UserAgent, @FailureReason, @OccurredAt,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(history), ct);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        // 보존정리: 기준시각 이전 로그인실패 이력을 삭제한다. 기준시각은 호출부(C#)에서 산정해 파라미터로 넘겨
        // MSSQL/SQLite 날짜 방언 분기를 피한다. DeleteAsync는 감사 미주입 raw 실행이며 영향 행 수를 반환한다.
        const string sql = "DELETE FROM SYS_LOGIN_FAILURE_HIST WHERE OCCURRED_AT < @cutoff";
        return await _processor.DeleteAsync(sql, new { cutoff }, ct);
    }

    public async Task<IReadOnlyList<LoginFailureHistory>> GetRecentByUserAsync(
        string userId, int count, CancellationToken ct = default)
    {
        var sql = _dialect.WrapPaged(
            "SELECT * FROM SYS_LOGIN_FAILURE_HIST WHERE USER_ID = @userId",
            "OCCURRED_AT DESC", 0, count);
        var rows = await QueryAsync<Row>(sql, new { userId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private sealed class Row
    {
        public string FailureId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string UserAgent { get; set; } = "";
        public string FailureReason { get; set; } = "";
        public DateTime OccurredAt { get; set; }

        public LoginFailureHistory ToDomain()
            => LoginFailureHistory.Restore(FailureId, UserId, IpAddress, UserAgent, FailureReason, OccurredAt);

        public static Row FromDomain(LoginFailureHistory h) => new()
        {
            FailureId = h.Id,
            UserId = h.UserId,
            IpAddress = h.IpAddress,
            UserAgent = h.UserAgent,
            FailureReason = h.FailureReason,
            OccurredAt = h.OccurredAt
        };
    }
}
