# Build from the repository root: docker build -f src/Shop.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from the project files alone, so this layer stays cached until a dependency changes. The worker needs only
# these: no Orders, Users or Products implementation, so it can't reach their tables
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Shop.Worker/Shop.Worker.csproj src/Shop.Worker/
COPY src/Shop.SharedKernel/Shop.SharedKernel.csproj src/Shop.SharedKernel/
COPY src/Shop.Messaging/Shop.Messaging.csproj src/Shop.Messaging/
COPY src/Modules/Notifications/Shop.Modules.Notifications/Shop.Modules.Notifications.csproj src/Modules/Notifications/Shop.Modules.Notifications/
COPY src/Modules/Notifications/Shop.Modules.Notifications.Contracts/Shop.Modules.Notifications.Contracts.csproj src/Modules/Notifications/Shop.Modules.Notifications.Contracts/
COPY src/Modules/Orders/Shop.Modules.Orders.Contracts/Shop.Modules.Orders.Contracts.csproj src/Modules/Orders/Shop.Modules.Orders.Contracts/
RUN dotnet restore src/Shop.Worker/Shop.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Shop.Worker/Shop.Worker.csproj --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# The ASP.NET Core runtime, as the Notifications module and SharedKernel reference that framework
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The image's built-in non-root user; the worker listens on no port
USER $APP_UID
ENTRYPOINT ["dotnet", "Shop.Worker.dll"]
