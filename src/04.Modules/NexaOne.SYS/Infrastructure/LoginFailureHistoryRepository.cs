using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class LoginFailureHistoryRepository : QueryRepository, ILoginFailureHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;

    public LoginFailureHistoryRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
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

    public async Task<IReadOnlyList<LoginFailureHistory>> GetRecentByUserAsync(
        string userId, int count, CancellationToken ct = default)
    {
        const string sql = @"SELECT TOP (@count) * FROM SYS_LOGIN_FAILURE_HIST WITH(NOLOCK)
            WHERE USER_ID = @userId ORDER BY OCCURRED_AT DESC";
        var rows = await QueryAsync<Row>(sql, new { userId, count }, ct);
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
