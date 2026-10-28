using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shop.SharedKernel.Errors;

internal sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            // A request that can't be read: Minimal APIs throw this in Development, Kestrel for an oversized body
            BadHttpRequestException badRequest => (badRequest.StatusCode, ErrorCodes.ForStatus(badRequest.StatusCode), null),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ErrorCodes.Common.ConcurrencyConflict,
                "Someone else changed this resource; reload it and try again."),
            _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Common.InternalError, (string?)null),
        };

        logger.Log(
            status >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Information,
            exception,
            "{Method} {Path} failed with {Status} {Code}",
            httpContext.Request.Method, httpContext.Request.Path, status, code);

        var problem = new ProblemDetails { Status = status, Detail = detail, Extensions = { ["code"] = code } };
        if (environment.IsDevelopment())
        {
            problem.Extensions["exception"] = exception.ToString();
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
