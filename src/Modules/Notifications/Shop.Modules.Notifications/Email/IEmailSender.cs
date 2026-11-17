namespace Shop.Modules.Notifications.Email;

/// <summary>
/// Hands an email to the mail provider (<see cref="SmtpEmailSender"/>). Throws when it can't; the message then goes
/// back to RabbitMQ and is tried again.
/// </summary>
internal interface IEmailSender
{
    Task SendAsync(EmailMessage email, CancellationToken cancellationToken);
}
