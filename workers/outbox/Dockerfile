# OpenConnect Outbox relay host: publishes committed outbox entries to JetStream.
# Build context is the repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/OpenConnect.Abstractions/ src/OpenConnect.Abstractions/
COPY src/OpenConnect.Core/ src/OpenConnect.Core/
COPY src/OpenConnect.Persistence.EFCore/ src/OpenConnect.Persistence.EFCore/
COPY src/OpenConnect.Transport.Nats/ src/OpenConnect.Transport.Nats/
COPY workers/outbox/ workers/outbox/
RUN dotnet publish workers/outbox/OpenConnect.WorkerHosts.Outbox.csproj \
    -c Release -o /app/publish /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish ./
# Non-root execution. No model volume is mounted: this host only relays envelopes.
USER app
ENTRYPOINT ["dotnet", "OpenConnect.WorkerHosts.Outbox.dll"]
