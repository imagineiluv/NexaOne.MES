using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexaOne.Common.Telemetry;

namespace NexaOne.API.Hubs;

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

    public async Task JoinGroup(string groupName) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

    public async Task LeaveGroup(string groupName) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
}
