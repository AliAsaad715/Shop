# Build from the repository root: docker build -f src/Shop.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from the project files alone, so this layer stays cached until a dependency changes
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Shop.Api/Shop.Api.csproj src/Shop.Api/
COPY src/Shop.SharedKernel/Shop.SharedKernel.csproj src/Shop.SharedKernel/
COPY src/Modules/Users/Shop.Modules.Users/Shop.Modules.Users.csproj src/Modules/Users/Shop.Modules.Users/
COPY src/Modules/Users/Shop.Modules.Users.Contracts/Shop.Modules.Users.Contracts.csproj src/Modules/Users/Shop.Modules.Users.Contracts/
COPY src/Modules/Products/Shop.Modules.Products/Shop.Modules.Products.csproj src/Modules/Products/Shop.Modules.Products/
COPY src/Modules/Products/Shop.Modules.Products.Contracts/Shop.Modules.Products.Contracts.csproj src/Modules/Products/Shop.Modules.Products.Contracts/
COPY src/Modules/Orders/Shop.Modules.Orders/Shop.Modules.Orders.csproj src/Modules/Orders/Shop.Modules.Orders/
COPY src/Modules/Orders/Shop.Modules.Orders.Contracts/Shop.Modules.Orders.Contracts.csproj src/Modules/Orders/Shop.Modules.Orders.Contracts/
COPY src/Modules/Notifications/Shop.Modules.Notifications/Shop.Modules.Notifications.csproj src/Modules/Notifications/Shop.Modules.Notifications/
COPY src/Modules/Notifications/Shop.Modules.Notifications.Contracts/Shop.Modules.Notifications.Contracts.csproj src/Modules/Notifications/Shop.Modules.Notifications.Contracts/
RUN dotnet restore src/Shop.Api/Shop.Api.csproj

COPY src/ src/
RUN dotnet publish src/Shop.Api/Shop.Api.csproj --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Product images live on a volume mounted here, so the non-root user must own it
RUN mkdir -p /data/images && chown $APP_UID:$APP_UID /data/images

COPY --from=build /app/publish .

# The image's built-in non-root user; ASP.NET Core listens on 8080 by default
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
