using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace Shop.Modules.Notifications.Email;

/// <summary>
/// Sends each email over SMTP with MailKit, to Mailpit or a real provider depending on <see cref="EmailOptions.Provider"/>.
/// A new connection per email: simple, and enough for one email per order. Any failure throws, so the message goes back
/// to RabbitMQ and is tried again.
/// </summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Subject = email.Subject;
        message.Body = new TextPart(TextFormat.Plain) { Text = email.TextBody };

        // The same message sent twice (a crash between sending and recording it) carries the same Message-ID, so a
        // mail service or the customer can tell it is a repeat
        message.MessageId = $"{email.Id}@{((MailboxAddress)message.From[0]).Domain}";

        using var client = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
        if (settings.Provider == EmailProvider.Mailpit)
        {
            await client.ConnectAsync(settings.Mailpit.Host, settings.Mailpit.Port, SecureSocketOptions.None, cancellationToken);
        }
        else
        {
            await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, ToSocketOptions(settings.Smtp.Security), cancellationToken);
            await client.AuthenticateAsync(settings.Smtp.UserName, settings.Smtp.Password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None,
    };
}
