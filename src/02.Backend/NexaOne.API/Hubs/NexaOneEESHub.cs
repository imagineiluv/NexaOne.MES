using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NexaOne.API.Hubs;

[Authorize]
public sealed class NexaOneEESHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (userId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
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
