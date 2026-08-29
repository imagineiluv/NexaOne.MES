using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.SYS.Infrastructure;

/// <summary>SYS 사용자의 활성·미삭제 상태만 제공하는 owner adapter입니다.</summary>
public sealed class UserDirectory : QueryRepository, IUserDirectory
{
    public UserDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<bool> IsActiveAsync(
        string userId,
        CancellationToken ct = default)
        => await CountAsync(
            @"SELECT COUNT(*) FROM SYS_USER
              WHERE USER_ID = @userId AND IS_ACTIVE = 1 AND IS_DELETED = 0",
            new { userId },
            ct) > 0;
}
