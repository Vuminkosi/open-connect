# Dockerfile for OpenConnect Blazor Web & Gateway Sample App
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Copy Directory props, lockfiles, and solution configs
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props

# Copy sources and sample projects
COPY src/ src/
COPY samples/_shared/ samples/_shared/
COPY samples/Blazor/ samples/Blazor/

# Publish the Blazor sample in Release mode
RUN dotnet publish samples/Blazor/OpenConnect.Samples.Blazor.csproj \
    -c Release -o /app/publish /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./

# Create data directory for SQLite persistence
RUN mkdir -p /data && chown -R app:app /data /app
USER app

ENV ASPNETCORE_URLS=http://+:5000
ENV OPENCONNECT_BLAZOR_SQLITE_PATH=/data/blazor-todo.db

EXPOSE 5000
ENTRYPOINT ["dotnet", "OpenConnect.Samples.Blazor.dll"]
