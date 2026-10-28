using Microsoft.AspNetCore.Http;

namespace Shop.SharedKernel.Errors;

/// <summary>
/// Every <c>code</c> the API puts on an error. A code is <c>area.name</c>, and the Problem Details
/// <c>type</c> comes from the name: <c>orders.invalid_status_transition</c> has the type
/// <c>https://shop.local/errors/invalid-status-transition</c>.
/// </summary>
public static class ErrorCodes
{
    private const string TypeBaseUri = "https://shop.local/errors/";

    // Errors without a more specific code, including the framework's own (unknown route, missing token, rate limit)
    public static class Common
    {
        public const string BadRequest = "common.bad_request"; // 400: the body or a parameter can't be parsed
        public const string Unauthorized = "common.unauthorized"; // 401: no token, or an invalid or expired one
        public const string Forbidden = "common.forbidden"; // 403: logged in, wrong role
        public const string NotFound = "common.not_found"; // 404
        public const string MethodNotAllowed = "common.method_not_allowed"; // 405
        public const string Conflict = "common.conflict"; // 409
        public const string ConcurrencyConflict = "common.concurrency_conflict"; // 409: someone else saved the same row first
        public const string PayloadTooLarge = "common.payload_too_large"; // 413
        public const string UnsupportedMediaType = "common.unsupported_media_type"; // 415
        public const string ValidationFailed = "common.validation_failed"; // 422: the failures per field are in errors
        public const string TooManyRequests = "common.too_many_requests"; // 429
        public const string InternalError = "common.internal_error"; // 500: exception details only in Development
        public const string ServiceUnavailable = "common.service_unavailable"; // 503
        public const string Error = "common.error"; // any other status
    }

    public static class Users
    {
        public const string DuplicateEmail = "users.duplicate_email"; // 409, task 09
        public const string InvalidCredentials = "users.invalid_credentials"; // 401, task 10: the same for every login failure
    }

    public static class Products
    {
        public const string NotFound = "products.not_found"; // 404, task 16
        public const string ImageTooLarge = "products.image_too_large"; // 413, task 15
        public const string UnsupportedImageType = "products.unsupported_image_type"; // 415, task 15: not a JPEG, PNG or WebP
    }

    public static class Orders
    {
        public const string NotFound = "orders.not_found"; // 404, task 20: also for someone else's order
        public const string InvalidStatusTransition = "orders.invalid_status_transition"; // 409, task 21
        public const string InsufficientStock = "orders.insufficient_stock"; // 409, once Requirement 2's warehouse can refuse
    }

    // The code for an error that doesn't carry one, such as routing's bare 404
    internal static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => Common.BadRequest,
        StatusCodes.Status401Unauthorized => Common.Unauthorized,
        StatusCodes.Status403Forbidden => Common.Forbidden,
        StatusCodes.Status404NotFound => Common.NotFound,
        StatusCodes.Status405MethodNotAllowed => Common.MethodNotAllowed,
        StatusCodes.Status409Conflict => Common.Conflict,
        StatusCodes.Status413PayloadTooLarge => Common.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => Common.UnsupportedMediaType,
        StatusCodes.Status422UnprocessableEntity => Common.ValidationFailed,
        StatusCodes.Status429TooManyRequests => Common.TooManyRequests,
        StatusCodes.Status500InternalServerError => Common.InternalError,
        StatusCodes.Status503ServiceUnavailable => Common.ServiceUnavailable,
        _ => Common.Error,
    };

    internal static string TypeFor(string code) =>
        TypeBaseUri + code[(code.IndexOf('.') + 1)..].Replace('_', '-');
}
