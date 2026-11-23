using Shop.Messaging;
using Shop.Modules.Notifications;

var builder = Host.CreateApplicationBuilder(args);

// Named after the host, a container ID under Compose, so RabbitMQ's UI tells several workers apart (task R5-03)
builder.Services.AddShopMessaging($"shop-worker {Environment.MachineName}");
builder.Services.AddNotificationsWorker();

var host = builder.Build();

// Ctrl+C or SIGTERM (docker compose stop) stops it gracefully: the current email finishes first
await host.RunAsync();
