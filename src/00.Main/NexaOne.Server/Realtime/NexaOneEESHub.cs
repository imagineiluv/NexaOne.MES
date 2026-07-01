using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexaOne.Common.Telemetry;

namespace NexaOne.Server.Realtime;

/// <summary>SmartEES 실시간 SignalR 허브(/hubs/smartees). 폐기된 NexaOne.API에서 통합 호스트로 이식(회귀 복원).
/// SPA(createHub)가 access_token 쿼리로 연결하며, 개인 채널(user:{userId})·기능 그룹(설비/플랜트/대시보드)에 가입한다.</summary>
[Authorize]
public sealed class NexaOneEESHub : Hub
{
    private readonly ActiveUserTracker _activeUsers;

    public NexaOneEESHub(ActiveUserTracker activeUsers) => _activeUsers = activeUsers;

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
            // §17.5 nexames_active_users — SignalR 연결도 활동으로 집계
            _activeUsers.Touch(Context.User?.FindFirst("plantId")?.Value, userId);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        if (userId is not null)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user:{userId}");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>개인 채널 접두사. <c>user:{userId}</c> 그룹은 본인만 가입할 수 있다(IDOR 차단).</summary>
    private const string UserGroupPrefix = "user:";

    public async Task JoinGroup(string groupName)
    {
        // IDOR 차단 — 임의 클라이언트가 타인의 개인 채널(user:{otherId})에 가입해 표적 메시지를 가로채는 것을 막는다.
        // user: 접두사 그룹은 본인 채널(user:{현재 사용자})만 허용하고, 나머지(설비/플랜트/대시보드 등 기능 그룹)는 허용한다.
        if (groupName.StartsWith(UserGroupPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(groupName, $"{UserGroupPrefix}{Context.UserIdentifier}", StringComparison.Ordinal))
            throw new HubException("Cannot join another user's private group.");

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task LeaveGroup(string groupName) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
}
