using Shop.Api.Docs;
using Shop.Api.LivePages;
using Shop.Api.Routing;
using Shop.Api.Security;
using Shop.Messaging;
using Shop.Modules.Notifications;
using Shop.Modules.Orders;
using Shop.Modules.Products;
using Shop.Modules.Users;
using Shop.SharedKernel.Diagnostics;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services
    .AddUsersModule()
    .AddProductsModule()
    .AddOrdersModule()
    .AddNotificationsModule();

// RabbitMQ (task R4-01): connects in the background and declares the topology; Degraded while it's down
builder.Services.AddShopMessaging("shop-api");

builder.Services.AddErrorContract();
builder.Services.AddVersionedApiDocs();
builder.Services.AddHealthChecks();
builder.Services.AddBrowserSecurity();

var app = builder.Build();

// Every module's schema is current before the first request
await app.MigrateModuleDatabasesAsync();

// Unhandled exceptions and bare status codes (unknown route, missing token, rate limit) become Problem Details too
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseSecurityHeaders();

// Development only: X-Db-Queries says how many SQL commands a request sent, so an N+1 shows up as a number
if (app.Environment.IsDevelopment())
{
    app.UseDbQueryCountHeader();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseStatusCodePages();
// The live pages (task R4-08): static files at /app, with their own CSP, served without a token
app.UseLivePages();
app.UseRouting();
app.UseFrontendCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseRoutingErrors();
app.UseAuthorization();
app.UseProductUploads();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapApiDocs();
app.MapProductImages();
app.MapNotificationsEndpoints();
app.MapLivePages();

var api = app.MapVersionedApi();
api.MapUsersEndpoints();
api.MapProductsEndpoints();
api.MapOrdersEndpoints();

app.Run();

// Exposes the real host entry point to WebApplicationFactory without exposing module internals.
public partial class Program
{
}
