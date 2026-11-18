# OpenConnect Operational worker host: supervises operational navigator runs.
# Build context is the repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/OpenConnect.Abstractions/ src/OpenConnect.Abstractions/
COPY src/OpenConnect.Core/ src/OpenConnect.Core/
COPY src/OpenConnect.Navigator/ src/OpenConnect.Navigator/
COPY src/OpenConnect.Navigator.Operational/ src/OpenConnect.Navigator.Operational/
COPY src/OpenConnect.Persistence.EFCore/ src/OpenConnect.Persistence.EFCore/
COPY src/OpenConnect.WorkerHosts.Operational/ src/OpenConnect.WorkerHosts.Operational/
RUN dotnet publish src/OpenConnect.WorkerHosts.Operational/OpenConnect.WorkerHosts.Operational.csproj \
    -c Release -o /app/publish /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish ./
# Non-root execution. No model volume is mounted: this host runs no detectors.
USER app
ENTRYPOINT ["dotnet", "OpenConnect.WorkerHosts.Operational.dll"]
