using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Shop.Modules.Notifications.Email;
using Xunit;

namespace Shop.Modules.Notifications.IntegrationTests;

/// <summary>
/// How each kind of failed send is sorted (task R4-05): temporary ones are tried again, permanent ones go to the
/// dead-letter queue at once, and ones caused by our own settings go back to RabbitMQ without trying again.
/// </summary>
public sealed class EmailFailuresTests
{
    private static readonly MailboxAddress Customer = MailboxAddress.Parse("ada@shop.test");

    public static TheoryData<Exception, string> Failures => new()
    {
        { new SocketException((int)SocketError.ConnectionRefused), "Temporary" },
        { new IOException("Connection reset by peer"), "Temporary" },
        { new TimeoutException(), "Temporary" },
        { new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, "421 Try again later"), "Temporary" },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, Customer, "450 Mailbox busy"), "Temporary" },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, Customer, "550 No such user"), "Permanent" },
        { new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "554 Refused as spam"), "Permanent" },
        { new ParseException("Not an address", 0, 0), "Permanent" },
        { new AuthenticationException("535 Username and Password not accepted"), "Settings" },
        { new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxNameNotAllowed, Customer, "553 Sender not allowed"), "Settings" },
        { new NotSupportedException("The SMTP server does not support the STARTTLS extension."), "Settings" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void EachFailureIsSortedByWhetherTryingAgainCanHelp(Exception error, string kind) =>
        Assert.Equal(kind, EmailFailures.Classify(error).ToString());
}
