using Shop.Api.Docs;
using Shop.Api.Instances;
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

const string migrationArgument = "--migrate-and-seed";
const string importArgument = "--import-images";
var migrateAndSeed = args.Contains(migrationArgument, StringComparer.Ordinal);
var importImages = args.Contains(importArgument, StringComparer.Ordinal);
if (migrateAndSeed && importImages)
{
    Console.Error.WriteLine("Choose either --migrate-and-seed or --import-images, not both.");
    return 1;
}
// This standalone switch belongs to the entry point, not the key/value configuration provider.
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != migrationArgument && argument != importArgument).ToArray());
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// api1, api2 or api3 when Compose runs three instances behind Nginx (task R5-03); null otherwise
var instanceName = InstanceName.From(builder.Configuration);

builder.Services
    .AddUsersModule()
    .AddProductsModule()
    .AddOrdersModule()
    .AddNotificationsModule();

if (migrateAndSeed)
{
    // The API's realtime side has no database registration; the worker owns this fourth schema.
    builder.Services.AddNotificationsPersistence();
}

// RabbitMQ (task R4-01): connects in the background and declares the topology; Degraded while it's down. Each instance
// names its connection, so RabbitMQ's UI tells the three apart (R5-03)
builder.Services.AddShopMessaging(instanceName is null ? "shop-api" : $"shop-api {instanceName}");

builder.Services.AddErrorContract();
builder.Services.AddVersionedApiDocs();
builder.Services.AddHealthChecks();
builder.Services.AddBrowserSecurity();
builder.Services.AddReverseProxyHeaders();

await using var app = builder.Build();

if (importImages)
{
    try { return await app.Services.ImportProductImagesAsync() ? 0 : 1; }
    catch (Exception exception)
    {
        app.Logger.LogCritical(exception, "Image import failed; legacy images retained");
        return 1;
    }
}

if (migrateAndSeed)
{
    try
    {
        // Do not start this host: no listener, RabbitMQ connection, relay or consumer is needed to initialize data.
        await app.Services.EnsureProductImageStorageReadyAsync();
        await app.InitializeModuleDatabasesAsync();
        return 0;
    }
    catch (Exception exception)
    {
        app.Logger.LogCritical(exception, "Database initialization failed");
        return 1;
    }
}

// First, so every response says which instance answered, errors included (task R5-03)
app.UseInstanceHeader(instanceName);
// Establish the trusted client address and scheme before HTTPS, security headers and the login limiter (R5-05).
app.UseReverseProxyHeaders();

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

await app.RunAsync();
return 0;

// Exposes the real host entry point to WebApplicationFactory without exposing module internals.
public partial class Program
{
}
