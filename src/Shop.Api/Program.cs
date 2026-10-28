using Shop.Modules.Notifications;
using Shop.Modules.Orders;
using Shop.Modules.Products;
using Shop.Modules.Users;
using Shop.SharedKernel.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddUsersModule()
    .AddProductsModule()
    .AddOrdersModule()
    .AddNotificationsModule();

builder.Services.AddHealthChecks();

var app = builder.Build();

// Every module's schema is current before the first request
await app.MigrateModuleDatabasesAsync();

app.MapHealthChecks("/health");

// Task 06 turns this into an Asp.Versioning route group
var api = app.MapGroup("/api/v1");
api.MapUsersEndpoints();
api.MapProductsEndpoints();
api.MapOrdersEndpoints();

app.Run();
