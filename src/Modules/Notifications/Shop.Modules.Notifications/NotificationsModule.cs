using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shop.Messaging;
using Shop.Messaging.Consuming;
using Shop.Modules.Notifications.Data;
using Shop.Modules.Notifications.Email;
using Shop.Modules.Notifications.OrderConfirmation;
using Shop.Modules.Notifications.Processing;
using Shop.Modules.Notifications.Realtime;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Notifications;

public static class NotificationsModule
{
    /// <summary>
    /// The API's secured hubs and live order subscription. Needs <see cref="MessagingRegistration.AddShopMessaging"/>;
    /// a missing broker degrades the subscription without delaying API startup.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddSignalR();
        // JWT keeps its original claim names, so SignalR must use sub rather than NameIdentifier.
        services.AddSingleton<IUserIdProvider, SubUserIdProvider>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RecentRealtimeMessages>();
        services.AddSingleton<LiveUpdateHandler>();
        services.AddSingleton<LiveUpdateState>();
        services.AddHostedService<LiveUpdateConsumer>();
        services.AddHealthChecks().AddCheck<LiveUpdateHealthCheck>("realtime");
        return services;
    }

    /// <summary>
    /// The email worker's side of the module (task R4-03), for the separate worker host: its own
    /// <c>notifications</c> tables and the consumer of the order confirmation queue. Needs
    /// <see cref="MessagingRegistration.AddShopMessaging"/>. The API never calls this.
    /// </summary>
    public static IServiceCollection AddNotificationsWorker(this IServiceCollection services)
    {
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.Schema);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ProcessedMessages>();

        // Logs instead of sending until task R4-04 adds the mail provider
        services.TryAddSingleton<IEmailSender, LogOnlyEmailSender>();
        services.AddQueueConsumer<OrderConfirmationHandler>(ShopTopology.EmailQueue, prefetch: 5);
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<OrdersHub>("/hubs/orders", options => options.CloseOnAuthenticationExpiration = true);
        endpoints.MapHub<AdminHub>("/hubs/admin", options => options.CloseOnAuthenticationExpiration = true);
        return endpoints;
    }
}
