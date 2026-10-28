using Shop.Api.Docs;
using Shop.Api.Routing;
using Shop.Modules.Notifications;
using Shop.Modules.Orders;
using Shop.Modules.Products;
using Shop.Modules.Users;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddUsersModule()
    .AddProductsModule()
    .AddOrdersModule()
    .AddNotificationsModule();

builder.Services.AddErrorContract();
builder.Services.AddVersionedApiDocs();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Every module's schema is current before the first request
await app.MigrateModuleDatabasesAsync();

// Unhandled exceptions and bare status codes (unknown route, missing token, rate limit) become Problem Details too
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseRoutingErrors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseProductUploads();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapApiDocs();
app.MapProductImages();

var api = app.MapVersionedApi();
api.MapUsersEndpoints();
api.MapProductsEndpoints();
api.MapOrdersEndpoints();

app.Run();

// Exposes the real host entry point to WebApplicationFactory without exposing module internals.
public partial class Program
{
}
