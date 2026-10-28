using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Api.ConformanceTests;

public sealed class ErrorPipelineFixture : IAsyncLifetime
{
    private WebApplication _app = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddErrorContract();
        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.UseStatusCodePages();

        // Test-only status producers exercise the production formatter, not authentication or rate-limit enforcement.
        _app.MapGet("/status/{status:int}", (int status) => TypedResults.StatusCode(status));
        _app.MapGet("/bound/{number}", (int number) => TypedResults.Ok(number));
        _app.MapGet("/throw", (Func<IResult>)(() => throw new InvalidOperationException("Private conformance exception.")));

        await _app.StartAsync();
        Client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

public sealed class ErrorPipelineTests(ErrorPipelineFixture fixture) : IClassFixture<ErrorPipelineFixture>
{
    [Theory]
    [InlineData(400, ErrorCodes.Common.BadRequest)]
    [InlineData(401, ErrorCodes.Common.Unauthorized)]
    [InlineData(403, ErrorCodes.Common.Forbidden)]
    [InlineData(404, ErrorCodes.Common.NotFound)]
    [InlineData(405, ErrorCodes.Common.MethodNotAllowed)]
    [InlineData(409, ErrorCodes.Common.Conflict)]
    [InlineData(413, ErrorCodes.Common.PayloadTooLarge)]
    [InlineData(415, ErrorCodes.Common.UnsupportedMediaType)]
    [InlineData(422, ErrorCodes.Common.ValidationFailed)]
    [InlineData(429, ErrorCodes.Common.TooManyRequests)]
    [InlineData(503, ErrorCodes.Common.ServiceUnavailable)]
    public async Task EveryErrorStatusUsesTheProblemContract(int status, string code)
    {
        var path = $"/status/{status}";
        using var response = await fixture.Client.GetAsync(path);

        await ProblemAssertions.VerifyAsync(response, status, code, path);
    }

    [Fact]
    public async Task FrameworkParameterBindingErrorsUseTheSameContract()
    {
        const string path = "/bound/not-a-number";
        using var response = await fixture.Client.GetAsync(path);

        await ProblemAssertions.VerifyAsync(response, 400, ErrorCodes.Common.BadRequest, path);
    }

    [Fact]
    public async Task UnexpectedExceptionsReturn500WithoutLeakingTheException()
    {
        using var response = await fixture.Client.GetAsync("/throw");
        var problem = await ProblemAssertions.VerifyAsync(response, 500, ErrorCodes.Common.InternalError, "/throw");
        Assert.DoesNotContain("Private conformance exception", problem.GetRawText());
    }
}
