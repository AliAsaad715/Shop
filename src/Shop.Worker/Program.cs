using Shop.Messaging;
using Shop.Modules.Notifications;
using Shop.SharedKernel.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddShopMessaging("shop-worker");
builder.Services.AddNotificationsWorker();

var host = builder.Build();

// Only the notifications schema: the worker registers no other module, and the API migrates the rest
await host.MigrateModuleDatabasesAsync();

// Ctrl+C or SIGTERM (docker compose stop) stops it gracefully: the current email finishes first
await host.RunAsync();
