using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Users.Auth;
using Shop.SharedKernel.Auth;
using Xunit;

namespace Shop.Modules.Users.UnitTests;

public sealed class CurrentUserTests
{
    [Fact]
    public void OutsideARequestHasNoIdentity()
    {
        var user = new CurrentUser(new HttpContextAccessor());
        Assert.False(user.IsAuthenticated);
        Assert.Null(user.UserId);
        Assert.Null(user.Email);
        Assert.Empty(user.Roles);
        Assert.False(user.IsInRole(Roles.Admin));
    }

    [Fact]
    public void UnauthenticatedClaimsAreNotExposedAsIdentity()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", Guid.NewGuid().ToString()), new Claim("email", "untrusted@shop.test"), new Claim("role", Roles.Admin)])),
            },
        };
        var user = new CurrentUser(accessor);
        Assert.False(user.IsAuthenticated);
        Assert.Null(user.UserId);
        Assert.Null(user.Email);
        Assert.Empty(user.Roles);
        Assert.False(user.IsInRole(Roles.Admin));
        accessor.HttpContext = null;
    }

    [Fact]
    public void ReadsTheActiveRequestWithoutCachingAnotherUsersClaims()
    {
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var accessor = new HttpContextAccessor { HttpContext = Context(firstId, "first@shop.test", Roles.Admin, Roles.Admin) };
        var user = new CurrentUser(accessor);
        Assert.True(user.IsAuthenticated);
        Assert.Equal(firstId, user.UserId);
        Assert.Equal("first@shop.test", user.Email);
        Assert.Equal([Roles.Admin], user.Roles);
        Assert.True(user.IsInRole(Roles.Admin));
        Assert.False(user.IsInRole(Roles.Customer));

        accessor.HttpContext = Context(secondId, "second@shop.test", Roles.Customer);
        Assert.Equal(secondId, user.UserId);
        Assert.Equal("second@shop.test", user.Email);
        Assert.Equal([Roles.Customer], user.Roles);
        Assert.False(user.IsInRole(Roles.Admin));
        accessor.HttpContext = null;
        Assert.Null(user.UserId);
    }

    [Fact]
    public void CurrentUserIsRegisteredPerScope()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddLogging().AddUsersModule().BuildServiceProvider();
        using var first = services.CreateScope();
        using var second = services.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<ICurrentUser>(), first.ServiceProvider.GetRequiredService<ICurrentUser>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<ICurrentUser>(), second.ServiceProvider.GetRequiredService<ICurrentUser>());
    }

    private static DefaultHttpContext Context(Guid id, string email, params string[] roles) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", id.ToString()), new Claim("email", email) }.Concat(roles.Select(role => new Claim("role", role))),
            "Bearer", "email", "role")),
    };
}
