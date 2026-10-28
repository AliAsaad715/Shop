using Shop.Modules.Notifications;
using Shop.Modules.Orders;
using Shop.Modules.Products;
using Shop.Modules.Users;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddUsersModule()
    .AddProductsModule()
    .AddOrdersModule()
    .AddNotificationsModule();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

// Task 06 turns this into an Asp.Versioning route group
var api = app.MapGroup("/api/v1");
api.MapUsersEndpoints();
api.MapProductsEndpoints();
api.MapOrdersEndpoints();

app.Run();
