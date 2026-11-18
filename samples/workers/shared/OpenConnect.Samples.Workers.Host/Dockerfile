# Shared sample gateway host for OpenConnect worker E2E samples.
# Build context is the repository root. Never downloads ML models or checkpoints.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/ src/
COPY samples/workers/shared/OpenConnect.Samples.Workers.Contracts/ samples/workers/shared/OpenConnect.Samples.Workers.Contracts/
COPY samples/workers/shared/OpenConnect.Samples.Workers.Host/ samples/workers/shared/OpenConnect.Samples.Workers.Host/
RUN dotnet publish samples/workers/shared/OpenConnect.Samples.Workers.Host/OpenConnect.Samples.Workers.Host.csproj \
    -c Release -o /app/publish /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenConnect.Samples.Workers.Host.dll"]
