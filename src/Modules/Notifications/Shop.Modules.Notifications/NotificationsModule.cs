using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
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

        // Mailpit or a real SMTP provider, by Email:Provider (task R4-04). A wrong Email section stops the worker at
        // startup rather than at its first email
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.Section)
            .Validate(EmailOptions.HasValidFrom, "Set Email__From to the sender, such as \"Shop <orders@shop.local>\".")
            .Validate(EmailOptions.HasServer,
                "Email__Provider must be Mailpit (with Email__Mailpit__Host and Port) or Smtp (with Email__Smtp__Host, Port, " +
                "UserName and Password).")
            .Validate(EmailOptions.HasValidTimeout, "Email:Timeout must be more than 0 and at most 60 seconds.")
            .Validate(EmailOptions.HasValidRetries, "Email:SendAttempts must be 1 to 5, and Email:RetryDelay 0 to 10 seconds.")
            .ValidateOnStart();
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddQueueConsumer<OrderConfirmationHandler>(ShopTopology.EmailQueue, prefetch: 5);
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<OrdersHub>("/hubs/orders", HubOptions);
        endpoints.MapHub<AdminHub>("/hubs/admin", HubOptions);
        return endpoints;
    }

    // WebSockets only (task R5-10). Behind Nginx, with no sticky sessions, each request can reach a different instance:
    // a WebSocket is one connection, but long polling and server-sent events take several requests that must all reach
    // the instance holding the connection, so they would fail at random instead of not at all
    private static void HubOptions(HttpConnectionDispatcherOptions options)
    {
        options.CloseOnAuthenticationExpiration = true;
        options.Transports = HttpTransportType.WebSockets;
    }
}
