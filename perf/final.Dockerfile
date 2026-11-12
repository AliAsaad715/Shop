# The runtime stage of src/Shop.Api/Dockerfile, for an app perf/final.sh has already published into publish/ (task
# R3-12). The build stage needs NuGet's servers, so final.sh publishes with the same SDK image and this machine's
# package cache instead, the same way for every step.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Product images live on a volume mounted here, so the non-root user must own it
RUN mkdir -p /data/images && chown $APP_UID:$APP_UID /data/images

COPY publish/ .

# The image's built-in non-root user; ASP.NET Core listens on 8080 by default
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
