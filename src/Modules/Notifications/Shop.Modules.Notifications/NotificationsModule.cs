using Microsoft.Extensions.DependencyInjection;

namespace Shop.Modules.Notifications;

public static class NotificationsModule
{
    // Placeholder in Requirement 1: no endpoints and no tables, so no DbContext or MapNotificationsEndpoints yet
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        return services;
    }
}
