FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/ src/
COPY site/OpenConnect.Docs/ site/OpenConnect.Docs/
RUN dotnet restore site/OpenConnect.Docs/OpenConnect.Docs.csproj --locked-mode \
    && dotnet publish site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /home/app/.aspnet
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenConnect.Docs.dll"]
