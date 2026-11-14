using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Notifications.Realtime;

namespace Shop.Modules.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddSignalR();
        // JWT keeps its original claim names, so SignalR must use sub rather than NameIdentifier.
        services.AddSingleton<IUserIdProvider, SubUserIdProvider>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<OrdersHub>("/hubs/orders", options => options.CloseOnAuthenticationExpiration = true);
        endpoints.MapHub<AdminHub>("/hubs/admin", options => options.CloseOnAuthenticationExpiration = true);
        return endpoints;
    }
}
