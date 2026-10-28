using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Api.ConformanceTests;

[Collection("API conformance")]
public sealed class ApiContractTests(ConformanceFixture fixture)
{
    [Fact]
    public async Task UnknownRoutesReturn404ProblemDetails()
    {
        const string path = "/api/v1/no-such-resource";
        using var response = await fixture.Client.GetAsync(path);

        await ProblemAssertions.VerifyAsync(response, 404, ErrorCodes.Common.NotFound, path);
    }

    [Fact]
    public async Task UnsupportedMethodsReturn405ProblemDetails()
    {
        const string path = "/api/v1/auth/register";
        using var response = await fixture.Client.GetAsync(path);

        await ProblemAssertions.VerifyAsync(response, 405, ErrorCodes.Common.MethodNotAllowed, path);
    }

    [Fact]
    public async Task MalformedJsonReturns400ProblemDetails()
    {
        const string path = "/api/v1/auth/register";
        using var response = await fixture.Client.PostAsync(path, new StringContent("{", System.Text.Encoding.UTF8, "application/json"));

        await ProblemAssertions.VerifyAsync(response, 400, ErrorCodes.Common.BadRequest, path);
    }

    [Fact]
    public async Task ValidationReturns422WithCamelCaseFieldErrors()
    {
        const string path = "/api/v1/auth/register";
        using var response = await fixture.Client.PostAsJsonAsync(path, new { email = "invalid", password = "" });

        var problem = await ProblemAssertions.VerifyAsync(response, 422, ErrorCodes.Common.ValidationFailed, path);
        Assert.True(problem.GetProperty("errors").TryGetProperty("email", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task DuplicateEmailReturnsTheUsers409Contract()
    {
        const string path = "/api/v1/auth/register";
        var account = new { email = $"conformance-{Guid.NewGuid():N}@shop.test", password = "Customer123!" };
        using var created = await fixture.Client.PostAsJsonAsync(path, account);
        Assert.Equal(201, (int)created.StatusCode);
        using var duplicate = await fixture.Client.PostAsJsonAsync(path, account);

        await ProblemAssertions.VerifyAsync(duplicate, 409, ErrorCodes.Users.DuplicateEmail, path);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("999")]
    public async Task UnsupportedApiVersionsAreRejected(string version)
    {
        var path = $"/api/v{version}/auth/register";
        using var response = await fixture.Client.PostAsJsonAsync(path, new { email = "unused@shop.test", password = "Customer123!" });

        // URL-segment versioning rejects unsupported versions with 404, rather than query/header versioning's 400.
        await ProblemAssertions.VerifyAsync(response, 404, ErrorCodes.Common.NotFound, path);
    }

    [Fact]
    public async Task MissingVersionDoesNotFallBackToVersionOne()
    {
        const string path = "/api/auth/register";
        using var response = await fixture.Client.PostAsJsonAsync(path, new { email = "unused@shop.test", password = "Customer123!" });

        await ProblemAssertions.VerifyAsync(response, 404, ErrorCodes.Common.NotFound, path);
    }

    [Fact]
    public async Task DatabaseFailureReturnsSanitized500ProblemDetails()
    {
        const string path = "/api/v1/auth/register";
        var configuration = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        var original = configuration.GetConnectionString("Shop");
        try
        {
            // Startup migrations already ran. Only this serialized request points at a nonexistent test database.
            configuration["ConnectionStrings:Shop"] = new NpgsqlConnectionStringBuilder(original)
            {
                Database = "shop_conformance_missing_" + Guid.NewGuid().ToString("N"),
                Timeout = 1,
            }.ConnectionString;
            using var response = await fixture.Client.PostAsJsonAsync(path,
                new { email = $"fault-{Guid.NewGuid():N}@shop.test", password = "Customer123!" });

            var problem = await ProblemAssertions.VerifyAsync(response, 500, ErrorCodes.Common.InternalError, path);
            Assert.DoesNotContain("Npgsql", problem.GetRawText());
            Assert.DoesNotContain("shop_conformance_missing_", problem.GetRawText());
        }
        finally
        {
            configuration["ConnectionStrings:Shop"] = original;
        }
    }
}
