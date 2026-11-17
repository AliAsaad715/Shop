using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Shop.Modules.Notifications.Email;

/// <summary>Why an email couldn't be sent, which decides whether trying again can help.</summary>
internal enum EmailFailure
{
    /// <summary>
    /// Likely to pass soon: the server is down, slow or busy (any 4xx reply). Tried again in the worker, then counted
    /// by RabbitMQ.
    /// </summary>
    Temporary,

    /// <summary>
    /// This message can never be sent: a malformed address, or a 5xx refusal of the recipient or the content. Sent
    /// straight to the dead-letter queue.
    /// </summary>
    Permanent,

    /// <summary>
    /// Our settings are wrong (password, sender address, TLS), so every email fails the same way until someone fixes
    /// them. Counted by RabbitMQ without trying again in the worker, as repeated failed sign-ins can get an account
    /// locked; the messages reach the dead-letter queue and go back once the settings are fixed.
    /// </summary>
    Settings,
}

internal static class EmailFailures
{
    public static EmailFailure Classify(Exception error) => error switch
    {
        // MailboxAddress.Parse refused the customer's address
        ParseException => EmailFailure.Permanent,
        SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted } refused
            when IsPermanent(refused.StatusCode) => EmailFailure.Permanent,
        AuthenticationException or SslHandshakeException or NotSupportedException => EmailFailure.Settings,
        SmtpCommandException refused when IsPermanent(refused.StatusCode) => EmailFailure.Settings,

        // Network errors, timeouts and 4xx replies
        _ => EmailFailure.Temporary,
    };

    // 5xx replies are permanent in SMTP; 4xx ones are worth another try
    private static bool IsPermanent(SmtpStatusCode status) => (int)status >= 500;
}
