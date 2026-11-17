using System.Globalization;
using System.Text;
using Shop.Modules.Notifications.Email;
using Shop.Modules.Orders.Contracts;

namespace Shop.Modules.Notifications.OrderConfirmation;

/// <summary>The confirmation a customer gets once their order is saved, built only from the event.</summary>
internal static class OrderConfirmationEmail
{
    /// <param name="messageId">The RabbitMQ message it answers, which becomes the email's Message-ID.</param>
    public static EmailMessage From(OrderPlacedEvent order, string to, Guid messageId)
    {
        var amounts = CultureInfo.InvariantCulture;
        var body = new StringBuilder()
            .AppendLine("Hello,")
            .AppendLine()
            .AppendLine("Thank you for your order. We have received it and will let you know when it ships.")
            .AppendLine()
            .AppendLine(amounts, $"Order: {order.OrderId}")
            .AppendLine(amounts, $"Placed: {order.PlacedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC")
            .AppendLine();
        foreach (var line in order.Lines)
        {
            body.AppendLine(amounts, $"  {line.Quantity} x {line.ProductName} at {line.UnitPrice:0.00} = {line.LineTotal:0.00}");
        }

        body.AppendLine()
            .AppendLine(amounts, $"Total: {order.Total:0.00}")
            .AppendLine()
            .AppendLine("Shop");

        return new EmailMessage(messageId, to, $"We received your order {order.OrderId.ToString()[..8]}", body.ToString());
    }
}
