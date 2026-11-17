using System.Net.Sockets;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shop.Modules.Notifications.Email;
using Xunit;

namespace Shop.Modules.Notifications.IntegrationTests;

/// <summary>
/// The MailKit sender (task R4-04) over a real SMTP conversation: one switch picks Mailpit (no password) or a real
/// provider (signed in), a wrong password is refused, the default refuses to send a password unencrypted, and a wrong
/// Email section is caught when the worker starts.
/// </summary>
public sealed class SmtpEmailSenderTests
{
    private static readonly EmailMessage Email = new(
        Guid.Parse("019a2180-4000-7000-8000-000000000001"), "ada@shop.test", "We received your order 019a2180", "Order: 019a2180\r\nTotal: 59.97\r\n");

    [Fact]
    public async Task MailpitModeSendsWithoutSigningIn()
    {
        await using var server = new FakeSmtpServer();

        await Sender(new()
        {
            ["Email:Provider"] = "Mailpit", ["Email:Mailpit:Host"] = "127.0.0.1", ["Email:Mailpit:Port"] = $"{server.Port}",
        }).SendAsync(Email, CancellationToken.None);

        var received = Assert.Single(server.Received);
        Assert.Equal("\"Shop\" <orders@shop.local>", received.From.ToString());
        Assert.Equal("ada@shop.test", received.To.ToString());
        Assert.Equal("We received your order 019a2180", received.Subject);
        Assert.Equal("Order: 019a2180\nTotal: 59.97\n", received.TextBody?.ReplaceLineEndings("\n"));
        // The same message sent twice carries the same Message-ID
        Assert.Equal("019a2180-4000-7000-8000-000000000001@shop.local", received.MessageId);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("AUTH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SmtpModeSignsInAndSends()
    {
        await using var server = new FakeSmtpServer("shop@example.test", "app-password");

        await Sender(Smtp(server.Port, password: "app-password")).SendAsync(Email, CancellationToken.None);

        Assert.Single(server.Received);
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedAndNothingIsSent()
    {
        await using var server = new FakeSmtpServer("shop@example.test", "app-password");

        // R4-05 turns this into a counted retry and, in the end, the dead-letter queue
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            Sender(Smtp(server.Port, password: "wrong")).SendAsync(Email, CancellationToken.None));

        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task TheDefaultNeverSendsThePasswordToAServerThatCanNotEncrypt()
    {
        await using var server = new FakeSmtpServer("shop@example.test", "app-password");
        var settings = Smtp(server.Port, password: "app-password");
        settings.Remove("Email:Smtp:Security");

        // StartTls is the default, and this server offers no STARTTLS, so MailKit stops before signing in
        await Assert.ThrowsAsync<NotSupportedException>(() => Sender(settings).SendAsync(Email, CancellationToken.None));

        Assert.DoesNotContain(server.Commands, command => command.StartsWith("AUTH", StringComparison.Ordinal));
        Assert.Empty(server.Received);
    }

    [Theory]
    [InlineData("RCPT", "550 5.1.1 No such user", "Permanent")]
    [InlineData("RCPT", "452 4.2.2 Mailbox full, try later", "Temporary")]
    [InlineData("MAIL", "421 4.7.0 Too busy, try again later", "Temporary")]
    [InlineData("MAIL", "553 5.7.1 Sender address not allowed", "Settings")]
    [InlineData(FakeSmtpServer.AfterContent, "554 5.7.1 Message refused as spam", "Permanent")]
    [InlineData("DATA", "554 5.5.1 Transaction failed", "Settings")]
    public async Task ARealServersRefusalIsSortedIntoTheRightKind(string command, string reply, string kind)
    {
        await using var server = new FakeSmtpServer();
        server.Replies[command] = reply;

        var error = await Record.ExceptionAsync(() => Sender(new()
        {
            ["Email:Provider"] = "Mailpit", ["Email:Mailpit:Host"] = "127.0.0.1", ["Email:Mailpit:Port"] = $"{server.Port}",
        }).SendAsync(Email, CancellationToken.None));

        Assert.NotNull(error);
        Assert.Equal(kind, EmailFailures.Classify(error).ToString());
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task AServerThatIsDownIsATemporaryFailure()
    {
        var server = new FakeSmtpServer();
        var port = server.Port;
        await server.DisposeAsync();

        var error = await Record.ExceptionAsync(() => Sender(new()
        {
            ["Email:Provider"] = "Mailpit", ["Email:Mailpit:Host"] = "127.0.0.1", ["Email:Mailpit:Port"] = $"{port}",
        }).SendAsync(Email, CancellationToken.None));

        Assert.Equal("Temporary", EmailFailures.Classify(Assert.IsType<SocketException>(error)).ToString());
    }

    [Theory]
    [InlineData("Email:SendAttempts", "0")]
    [InlineData("Email:SendAttempts", "6")]
    [InlineData("Email:RetryDelay", "00:00:11")]
    [InlineData("Email:From", "not an address")]
    [InlineData("Email:Mailpit:Host", "")]
    [InlineData("Email:Mailpit:Port", "0")]
    [InlineData("Email:Timeout", "00:00:00")]
    public void AWrongMailpitSettingIsCaughtAtStartup(string key, string value) =>
        Assert.Throws<OptionsValidationException>(() => Options(new() { [key] = value }).Value);

    [Theory]
    [InlineData("Email:Smtp:Host")]
    [InlineData("Email:Smtp:UserName")]
    [InlineData("Email:Smtp:Password")]
    public void SmtpWithoutItsServerOrSignInIsCaughtAtStartup(string missing)
    {
        var settings = Smtp(587, password: "app-password");
        settings[missing] = "";

        Assert.Throws<OptionsValidationException>(() => Options(settings).Value);
    }

    [Fact]
    public void TheDefaultsAreValidAndUseMailpit()
    {
        var options = Options([]).Value;

        Assert.Equal(EmailProvider.Mailpit, options.Provider);
        Assert.Equal(SmtpSecurity.StartTls, options.Smtp.Security);
    }

    private static Dictionary<string, string?> Smtp(int port, string password) => new()
    {
        ["Email:Provider"] = "Smtp",
        ["Email:Smtp:Host"] = "127.0.0.1",
        ["Email:Smtp:Port"] = $"{port}",
        // Plain text only because this test server, like Mailpit, can't encrypt; the default is StartTls
        ["Email:Smtp:Security"] = "None",
        ["Email:Smtp:UserName"] = "shop@example.test",
        ["Email:Smtp:Password"] = password,
    };

    private static IEmailSender Sender(Dictionary<string, string?> settings) =>
        Services(settings).GetRequiredService<IEmailSender>();

    private static IOptions<EmailOptions> Options(Dictionary<string, string?> settings) =>
        Services(settings).GetRequiredService<IOptions<EmailOptions>>();

    // The worker's registration with the worker's defaults, which the settings override
    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Shop"] = "Host=127.0.0.1;Port=1;Database=unused",
                ["Email:From"] = "Shop <orders@shop.local>",
                ["Email:Timeout"] = "00:00:05",
            })
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddNotificationsWorker();
        return services.BuildServiceProvider();
    }
}
