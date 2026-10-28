using Shop.Modules.Notifications;
using Shop.Modules.Orders;
using Shop.Modules.Products;
using Shop.Modules.Users;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Persistence;
using Shop.SharedKernel.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddUsersModule()
    .AddProductsModule()
    .AddOrdersModule()
    .AddNotificationsModule();

builder.Services.AddErrorContract();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Every module's schema is current before the first request
await app.MigrateModuleDatabasesAsync();

// Unhandled exceptions and bare status codes (unknown route, missing token, rate limit) become Problem Details too
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health");

// Task 06 turns this into an Asp.Versioning route group; the validation filter stays on it
var api = app.MapGroup("/api/v1").WithRequestValidation();
api.MapUsersEndpoints();
api.MapProductsEndpoints();
api.MapOrdersEndpoints();

app.Run();
