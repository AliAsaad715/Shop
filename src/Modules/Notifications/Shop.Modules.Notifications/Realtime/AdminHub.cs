using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Notifications.Realtime;

[Authorize(Policy = Policies.AdminOnly)]
internal sealed class AdminHub : Hub
{
    internal const string GroupName = "admins";

    public override async Task OnConnectedAsync()
    {
        // Only the server assigns membership. A new connection (including a reconnect) joins again.
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
