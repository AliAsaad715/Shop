using Microsoft.AspNetCore.SignalR;

namespace Shop.Modules.Notifications.Realtime;

internal sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}
