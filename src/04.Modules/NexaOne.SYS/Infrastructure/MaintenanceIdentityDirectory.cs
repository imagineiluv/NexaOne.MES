using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.SYS.Infrastructure;

/// <summary>
/// 로그인 사용자와 보전 작업자 매핑을 해석해 EMS에 축소 identity를 제공하는 adapter입니다.
/// </summary>
public sealed class MaintenanceIdentityDirectory : QueryRepository, IMaintenanceIdentityDirectory
{
    public MaintenanceIdentityDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<MaintenanceIdentityEntry?> GetActiveIdentityAsync(
        string userId,
        DateTime at,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT u.USER_ID AS UserId, m.WORKER_ID AS WorkerId
              FROM SYS_USER u
              LEFT JOIN MDM_WORKER_USER_MAP m
                ON m.USER_ID=u.USER_ID AND m.IS_ACTIVE=1
               AND m.EFFECTIVE_FROM<=@at
               AND (m.EFFECTIVE_TO IS NULL OR m.EFFECTIVE_TO>@at)
             WHERE u.USER_ID=@userId AND u.IS_ACTIVE=1 AND u.IS_DELETED=0";
        var rows = await QueryAsync<IdentityRow>(sql, new { userId, at }, ct);
        if (rows.Count == 0) return null;
        if (rows.Count > 1)
        {
            throw new InvalidOperationException(
                $"User '{userId}' has multiple active maintenance-worker mappings at {at:O}.");
        }

        return new MaintenanceIdentityEntry(rows[0].UserId, rows[0].WorkerId);
    }

    private sealed class IdentityRow
    {
        public string UserId { get; set; } = string.Empty;
        public string? WorkerId { get; set; }
    }
}
