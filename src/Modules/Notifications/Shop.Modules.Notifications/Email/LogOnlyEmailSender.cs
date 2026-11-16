using Microsoft.Extensions.Logging;

namespace Shop.Modules.Notifications.Email;

/// <summary>
/// Stands in for a real mail provider until task R4-04: logs that the email would go out now. Only the recipient's
/// domain is logged, to keep addresses out of the logs.
/// </summary>
internal sealed class LogOnlyEmailSender(ILogger<LogOnlyEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        var domain = email.To[(email.To.LastIndexOf('@') + 1)..];
        logger.LogInformation("Email \"{Subject}\" to a recipient at {Domain} would be sent now (no mail provider until R4-04)",
            email.Subject, domain);
        return Task.CompletedTask;
    }
}
