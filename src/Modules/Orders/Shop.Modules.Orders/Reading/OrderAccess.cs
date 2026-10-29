using Microsoft.Extensions.Logging;
using Shop.Modules.Orders.Domain;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Orders.Reading;

/// <summary>
/// The OwnerOrAdmin rule. Someone else's order is answered exactly like a missing one (404), so a
/// customer can't learn which IDs exist, and the denied attempt goes to the security log.
/// </summary>
internal static class OrderAccess
{
    public const string SecurityLogCategory = "Shop.Modules.Orders.Security";

    public static bool CanView(Order order, ICurrentUser currentUser, ILogger securityLog)
    {
        if (currentUser.IsInRole(Roles.Admin) || (currentUser.UserId is { } userId && userId == order.CustomerId))
        {
            return true;
        }

        securityLog.LogWarning(
            "User {UserId} was denied order {OrderId}, which belongs to another customer; answered 404",
            currentUser.UserId, order.Id);

        return false;
    }
}
