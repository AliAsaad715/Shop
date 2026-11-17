using MimeKit;

namespace Shop.Modules.Notifications.Email;

/// <summary>Where the email worker sends mail, bound from the <c>Email</c> section.</summary>
internal sealed class EmailOptions
{
    public const string Section = "Email";

    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The one switch: <see cref="EmailProvider.Mailpit"/> catches every email (local runs and k6, whose thousands of
    /// orders use made-up addresses), <see cref="EmailProvider.Smtp"/> sends for real.
    /// </summary>
    public EmailProvider Provider { get; init; } = EmailProvider.Mailpit;

    /// <summary>The sender, such as <c>Shop &lt;orders@shop.local&gt;</c>. Gmail needs its own address here.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// How long one SMTP step (connect, sign in, send) may take. Below the worker's 20-second graceful stop, so an email
    /// being sent when the worker stops normally finishes.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tries per delivery for a temporary failure, such as the mail server being briefly down. Only when all of them
    /// fail does the message go back to RabbitMQ, which counts it toward the queue's limit of six deliveries.
    /// </summary>
    public int SendAttempts { get; init; } = 3;

    /// <summary>The wait before the second try; it doubles before each later one (1 s, then 2 s by default).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public MailpitOptions Mailpit { get; init; } = new();

    public SmtpOptions Smtp { get; init; } = new();

    public static bool HasValidFrom(EmailOptions options) =>
        MailboxAddress.TryParse(options.From, out var from) && from.Address.Contains('@');

    public static bool HasServer(EmailOptions options) => options.Provider switch
    {
        EmailProvider.Mailpit => !string.IsNullOrWhiteSpace(options.Mailpit.Host) && IsPort(options.Mailpit.Port),
        EmailProvider.Smtp => !string.IsNullOrWhiteSpace(options.Smtp.Host) && IsPort(options.Smtp.Port)
            && !string.IsNullOrWhiteSpace(options.Smtp.UserName) && !string.IsNullOrEmpty(options.Smtp.Password),
        _ => false,
    };

    public static bool HasValidTimeout(EmailOptions options) => options.Timeout > TimeSpan.Zero && options.Timeout <= MaxTimeout;

    public static bool HasValidRetries(EmailOptions options) =>
        options.SendAttempts is >= 1 and <= 5 && options.RetryDelay >= TimeSpan.Zero && options.RetryDelay <= TimeSpan.FromSeconds(10);

    private static bool IsPort(int port) => port is > 0 and <= 65535;
}

internal enum EmailProvider
{
    Mailpit,
    Smtp,
}

/// <summary>Mailpit's SMTP port: no TLS and no password, and every email stays in its inbox at port 8025.</summary>
internal sealed class MailpitOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 1025;
}

/// <summary>A real provider. For Gmail: <c>smtp.gmail.com</c>, port 587, the Gmail address and an app password.</summary>
internal sealed class SmtpOptions
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 587;

    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}

internal enum SmtpSecurity
{
    /// <summary>Port 587: connect in plain text, then upgrade to TLS before signing in. The connection fails if the
    /// server can't upgrade, so the password never travels unencrypted.</summary>
    StartTls,

    /// <summary>Port 465: TLS from the first byte.</summary>
    SslOnConnect,

    /// <summary>No TLS at all, so the password travels in plain text. Only for a server on the same machine or
    /// network, such as the tests' Mailpit.</summary>
    None,
}
