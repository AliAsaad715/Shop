namespace Shop.Modules.Notifications.Email;

/// <summary>A plain-text email to one recipient.</summary>
/// <param name="Id">The RabbitMQ message it answers. It becomes the email's Message-ID, so a repeat carries the same one.</param>
internal sealed record EmailMessage(Guid Id, string To, string Subject, string TextBody);
