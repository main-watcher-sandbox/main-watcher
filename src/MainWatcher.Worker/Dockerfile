# The trigger worker (ADR-010). Build from the repository root:
#   docker build -f src/MainWatcher.Worker/Dockerfile -t main-watcher-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props ./
COPY src/MainWatcher.Core/MainWatcher.Core.csproj src/MainWatcher.Core/
COPY src/MainWatcher.Worker/MainWatcher.Worker.csproj src/MainWatcher.Worker/
RUN dotnet restore src/MainWatcher.Worker
COPY src/MainWatcher.Core/ src/MainWatcher.Core/
COPY src/MainWatcher.Worker/ src/MainWatcher.Worker/
RUN dotnet publish src/MainWatcher.Worker --no-restore --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The image's non-root user. The worker needs no writable filesystem and only outbound HTTPS.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MainWatcher.Worker.dll"]
