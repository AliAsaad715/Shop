using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Shop.Modules.Users.Auth;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Xunit;

namespace Shop.Modules.Users.UnitTests;

public sealed class JwtTokenTests
{
    [Fact]
    public async Task IssuedTokenHasVerifiableSignatureClaimsLifetimeAndUniqueId()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var services = Build(clock: new FrozenClock(now));
        var issuer = services.GetRequiredService<JwtTokenIssuer>();
        var user = new User { Email = "customer@shop.test" };
        var response = issuer.Issue(user, [Roles.Customer, Roles.Admin]);
        var jwt = new JsonWebToken(response.AccessToken);

        Assert.Equal("Bearer", response.TokenType);
        Assert.Equal(900, response.ExpiresIn);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal("shop", jwt.Issuer);
        Assert.Equal(["shop-api"], jwt.Audiences);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(user.Email, jwt.GetPayloadValue<string>("email"));
        Assert.Equal(new[] { Roles.Customer, Roles.Admin }, jwt.GetPayloadValue<string[]>("role"));
        Assert.Equal(now.UtcDateTime, jwt.IssuedAt);
        Assert.Equal(now.UtcDateTime, jwt.ValidFrom);
        Assert.Equal(now.AddMinutes(15).UtcDateTime, jwt.ValidTo);
        Assert.True(Guid.TryParse(jwt.Id, out _));
        Assert.NotEqual(jwt.Id, new JsonWebToken(issuer.Issue(user, [Roles.Customer]).AccessToken).Id);

        var validation = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer").TokenValidationParameters;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(response.AccessToken, validation);
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.False(services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer").MapInboundClaims);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("c2hvcnQ=")]
    public void MissingMalformedOrShortSigningKeysAreRejected(string key)
    {
        using var services = Build(new() { ["Jwt:SigningKey"] = key });
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    [Theory]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:LifetimeMinutes", "14")]
    [InlineData("Jwt:LifetimeMinutes", "31")]
    public void InvalidTokenConfigurationIsRejected(string key, string value)
    {
        using var services = Build(new() { [key] = value });
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    private static ServiceProvider Build(Dictionary<string, string?>? overrides = null, TimeProvider? clock = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        foreach (var (key, value) in overrides ?? [])
        {
            configuration[key] = value;
        }

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        return services.AddUsersModule().BuildServiceProvider();
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
