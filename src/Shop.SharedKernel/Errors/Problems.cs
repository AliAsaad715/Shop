using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Shop.SharedKernel.Errors;

public static class Problems
{
    /// <summary>
    /// An error response carrying <paramref name="code"/>; the error contract adds <c>type</c>,
    /// <c>instance</c> and <c>traceId</c>.
    /// </summary>
    public static ProblemHttpResult Create(int status, string code, string title, string? detail = null) =>
        TypedResults.Problem(detail, statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>422 with the failures per field, the same response the validation filter returns.</summary>
    public static ProblemHttpResult Validation(IDictionary<string, string[]> errors) =>
        TypedResults.Problem(new HttpValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Extensions = { ["code"] = ErrorCodes.Common.ValidationFailed },
        });
}
