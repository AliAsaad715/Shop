using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

[Collection("Users HTTP")]
public sealed class AuthorizationHttpTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Theory]
    [InlineData("default", null, 401)]
    [InlineData("default", Roles.Customer, 200)]
    [InlineData("default", Roles.Admin, 200)]
    [InlineData("admin", null, 401)]
    [InlineData("admin", Roles.Customer, 403)]
    [InlineData("admin", Roles.Admin, 200)]
    [InlineData("customer", null, 401)]
    [InlineData("customer", Roles.Customer, 200)]
    [InlineData("customer", Roles.Admin, 403)]
    [InlineData("admin", "admin", 403)]
    [InlineData("customer", "customer", 403)]
    [InlineData("admin", "Auditor", 403)]
    public async Task DefaultAndRolePoliciesEnforceTheAuthorizationMatrix(string route, string? role, int status)
    {
        using var response = await GetAsync("/test/" + route, role is null ? null : Token([role]));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (status is 401 or 403)
        {
            await AssertProblemAsync(response, status, status == 401 ? ErrorCodes.Common.Unauthorized : ErrorCodes.Common.Forbidden);
        }
    }

    [Fact]
    public async Task AUserWithBothRolesCanUseBothPolicies()
    {
        var token = Token([Roles.Customer, Roles.Admin]);
        foreach (var route in new[] { "/test/customer", "/test/admin" })
        {
            using var response = await GetAsync(route, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task CurrentUserComesFromValidatedClaimsAndIgnoresQueryValues()
    {
        var id = Guid.CreateVersion7();
        var token = Token([Roles.Customer], descriptor => descriptor.Claims["sub"] = id.ToString());
        using var response = await GetAsync($"/test/current?userId={Guid.NewGuid()}&role=Admin&email=other@shop.test", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var current = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(current.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(id, current.GetProperty("userId").GetGuid());
        Assert.Equal("user@shop.test", current.GetProperty("email").GetString());
        Assert.Equal([Roles.Customer], current.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.False(current.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task AllowAnonymousOverridesTheFallbackPolicy()
    {
        using var response = await GetAsync("/test/public", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var current = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(current.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("userId").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("email").ValueKind);
        Assert.Empty(current.GetProperty("roles").EnumerateArray());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("missing-expiration")]
    [InlineData("wrong-signature")]
    [InlineData("unsigned")]
    [InlineData("wrong-algorithm")]
    [InlineData("wrong-type")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("audience-trailing-slash")]
    [InlineData("missing-audience")]
    [InlineData("missing-subject")]
    [InlineData("invalid-subject")]
    [InlineData("empty-guid-subject")]
    [InlineData("duplicate-subject")]
    [InlineData("missing-email")]
    [InlineData("empty-email")]
    [InlineData("missing-token-id")]
    [InlineData("missing-role")]
    [InlineData("empty-role")]
    public async Task InvalidTokensAreRejectedBeforeAnUnannotatedEndpointRuns(string scenario)
    {
        var token = Token([Roles.Admin], descriptor =>
        {
            switch (scenario)
            {
                case "expired": descriptor.Expires = DateTime.UtcNow.AddMinutes(-1); break;
                case "not-yet-valid": descriptor.NotBefore = DateTime.UtcNow.AddMinutes(1); break;
                case "missing-expiration": descriptor.Expires = null; break;
                case "wrong-signature": descriptor.SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(new byte[64]), SecurityAlgorithms.HmacSha256); break;
                case "unsigned": descriptor.SigningCredentials = null; break;
                case "wrong-algorithm": descriptor.SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(api.SigningKey)), SecurityAlgorithms.HmacSha384); break;
                case "wrong-type": descriptor.TokenType = "id_token"; break;
                case "wrong-issuer": descriptor.Issuer = "other"; break;
                case "wrong-audience": descriptor.Audience = "other"; break;
                case "audience-trailing-slash": descriptor.Audience = "shop-api/"; break;
                case "missing-audience": descriptor.Audience = null; break;
                case "missing-subject": descriptor.Claims.Remove("sub"); break;
                case "invalid-subject": descriptor.Claims["sub"] = "invalid"; break;
                case "empty-guid-subject": descriptor.Claims["sub"] = Guid.Empty.ToString(); break;
                case "duplicate-subject": descriptor.Claims["sub"] = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() }; break;
                case "missing-email": descriptor.Claims.Remove("email"); break;
                case "empty-email": descriptor.Claims["email"] = " "; break;
                case "missing-token-id": descriptor.Claims.Remove("jti"); break;
                case "missing-role": descriptor.Claims.Remove("role"); break;
                case "empty-role": descriptor.Claims["role"] = " "; break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        });

        using var response = await GetAsync("/test/default", token);
        await AssertProblemAsync(response, 401, ErrorCodes.Common.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer" && header.Parameter is null);
    }

    [Theory]
    [InlineData("recent-expiration")]
    [InlineData("near-future-start")]
    public async Task ThirtySecondClockSkewIsApplied(string scenario)
    {
        var token = Token([Roles.Customer], descriptor =>
        {
            if (scenario == "recent-expiration")
            {
                descriptor.Expires = DateTime.UtcNow.AddSeconds(-5);
            }
            else
            {
                descriptor.NotBefore = DateTime.UtcNow.AddSeconds(5);
            }
        });
        using var response = await GetAsync("/test/default", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private string Token(string[] roles, Action<SecurityTokenDescriptor>? configure = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "shop",
            Audience = "shop-api",
            TokenType = "JWT",
            IssuedAt = now.AddMinutes(-5),
            NotBefore = now.AddMinutes(-5),
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(api.SigningKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.CreateVersion7().ToString(),
                ["email"] = "user@shop.test",
                ["role"] = roles,
                ["jti"] = Guid.NewGuid().ToString(),
            },
        };
        configure?.Invoke(descriptor);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await api.Client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }
}
