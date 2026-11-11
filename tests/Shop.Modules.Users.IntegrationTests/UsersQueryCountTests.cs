using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Shop.SharedKernel.Diagnostics;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

// The Users host in Development, where every response says how many SQL commands its request sent (X-Db-Queries)
public sealed class DevelopmentAuthApi : IAsyncLifetime
{
    public AuthApi Api { get; } = new() { EnvironmentName = Environments.Development };

    public Task InitializeAsync() => Api.InitializeAsync();

    public Task DisposeAsync() => Api.DisposeAsync();
}

// Task R3-06: the current user's profile and roles are one projected query, not the whole user and then its roles
[Collection("Users HTTP")]
public sealed class UsersQueryCountTests(DevelopmentAuthApi host) : IClassFixture<DevelopmentAuthApi>
{
    private const string Password = "Counted123!x";

    [Fact]
    public async Task CurrentUserIsOneQuery()
    {
        var email = $"counted-{Guid.NewGuid():N}@shop.test";
        using (var registered = await host.Api.Client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password }))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            host.Api.TrackUser((await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        using var login = await host.Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.Api.Client.SendAsync(request);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, profile.GetProperty("email").GetString());
        Assert.Equal(["Customer"], profile.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal("1", Assert.Single(response.Headers.GetValues(DbQueryCountHeaderExtensions.HeaderName)));
    }
}
