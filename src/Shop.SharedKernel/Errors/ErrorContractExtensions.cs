using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Shop.SharedKernel.Errors;

public static class ErrorContractExtensions
{
    /// <summary>
    /// Makes every error RFC 9457 Problem Details with a <c>code</c>, a <c>type</c> derived from it,
    /// the request path as <c>instance</c>, and a <c>traceId</c>. The host still calls
    /// <c>UseExceptionHandler()</c> and <c>UseStatusCodePages()</c>, so unhandled exceptions and
    /// bare status codes get the same shape.
    /// </summary>
    public static IServiceCollection AddErrorContract(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ApplyContract);
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        return services;
    }

    private static void ApplyContract(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var httpContext = context.HttpContext;

        if (!problem.Extensions.TryGetValue("code", out var value) || value is not string code)
        {
            code = ErrorCodes.ForStatus(problem.Status ?? httpContext.Response.StatusCode);
            problem.Extensions["code"] = code;
        }

        problem.Type = ErrorCodes.TypeFor(code);
        problem.Instance ??= httpContext.Request.Path;
        problem.Extensions.TryAdd("traceId", Activity.Current?.Id ?? httpContext.TraceIdentifier);
    }
}
