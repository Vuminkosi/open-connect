# Shared E2E test runner container for OpenConnect worker samples (§4.4).
# Build context is the repository root. Never downloads ML models or checkpoints.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/ src/
COPY samples/_shared/ samples/_shared/
COPY samples/persistence/ samples/persistence/
COPY samples/workers/shared/ samples/workers/shared/
RUN dotnet build samples/workers/shared/OpenConnect.Samples.Workers.TestKit/OpenConnect.Samples.Workers.TestKit.csproj \
    -c Release /p:ContinuousIntegrationBuild=true
USER app
ENTRYPOINT ["dotnet", "test", "samples/workers/shared/OpenConnect.Samples.Workers.TestKit/OpenConnect.Samples.Workers.TestKit.csproj", "-c", "Release", "--no-build"]
