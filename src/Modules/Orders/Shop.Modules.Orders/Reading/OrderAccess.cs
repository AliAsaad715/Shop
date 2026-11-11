using Microsoft.Extensions.Logging;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Orders.Reading;

/// <summary>
/// The OwnerOrAdmin rule. Someone else's order is answered exactly like a missing one (404), so a
/// customer can't learn which IDs exist, and the denied attempt goes to the security log.
/// </summary>
internal static class OrderAccess
{
    public const string SecurityLogCategory = "Shop.Modules.Orders.Security";

    public static bool CanView(Guid orderId, Guid customerId, ICurrentUser currentUser, ILogger securityLog)
    {
        if (currentUser.IsInRole(Roles.Admin) || (currentUser.UserId is { } userId && userId == customerId))
        {
            return true;
        }

        securityLog.LogWarning(
            "User {UserId} was denied order {OrderId}, which belongs to another customer; answered 404",
            currentUser.UserId, orderId);

        return false;
    }
}
