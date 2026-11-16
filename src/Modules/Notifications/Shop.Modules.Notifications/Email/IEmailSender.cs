namespace Shop.Modules.Notifications.Email;

/// <summary>
/// Hands an email to the mail provider. Throws when it can't; the message then goes back to RabbitMQ and is tried
/// again. Task R4-04 adds the MailKit sender; until then <see cref="LogOnlyEmailSender"/> stands in.
/// </summary>
internal interface IEmailSender
{
    Task SendAsync(EmailMessage email, CancellationToken cancellationToken);
}
