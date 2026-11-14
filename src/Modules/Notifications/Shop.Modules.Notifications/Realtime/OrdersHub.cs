using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Shop.Modules.Notifications.Realtime;

// Server-to-client only. R4-07 sends through Clients.User(sub); clients never choose an order to follow.
[Authorize]
internal sealed class OrdersHub : Hub;
