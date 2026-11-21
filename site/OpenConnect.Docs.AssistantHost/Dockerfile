FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/ src/
COPY site/OpenConnect.Docs/Services/DocumentationGatewayVisitorTokens.cs site/OpenConnect.Docs/Services/DocumentationGatewayVisitorTokens.cs
COPY site/OpenConnect.Docs.AssistantHost/ site/OpenConnect.Docs.AssistantHost/
RUN dotnet restore site/OpenConnect.Docs.AssistantHost/OpenConnect.Docs.AssistantHost.csproj --locked-mode \
    && dotnet publish site/OpenConnect.Docs.AssistantHost/OpenConnect.Docs.AssistantHost.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
USER app
ENTRYPOINT ["dotnet", "OpenConnect.Docs.AssistantHost.dll"]
