using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shop.Modules.Users.Auth;
using Xunit;

namespace Shop.Modules.Users.UnitTests;

public sealed class LoginProtectionOptionsTests
{
    [Theory]
    [InlineData("LoginRateLimit:PermitLimit", "0")]
    [InlineData("LoginRateLimit:PermitLimit", "-1")]
    [InlineData("LoginRateLimit:WindowSeconds", "0")]
    [InlineData("LoginRateLimit:WindowSeconds", "-1")]
    [InlineData("LoginRateLimit:WindowSeconds", "86401")]
    public void InvalidLimitsAreRejected(string key, string value)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build())
            .AddUsersModule()
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<LoginRateLimitOptions>>().Value);
    }
}
