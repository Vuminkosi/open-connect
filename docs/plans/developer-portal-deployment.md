# Developer portal deployment

The official portal is the `site/OpenConnect.Docs` ASP.NET Core .NET 10 application. It is not the historical Astro/Starlight site and does not require a model provider, database, NATS or worker to serve static documentation.

## Local validation

```powershell
dotnet restore site/OpenConnect.Docs/OpenConnect.Docs.csproj --locked-mode
dotnet build site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --no-restore -warnaserror
dotnet run --project site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --urls http://127.0.0.1:5187
```

Verify `/health/live`, `/health/ready`, `/`, `/docs`, a versioned document and `/sitemap.xml` before stopping the process.

## Reverse proxy

Terminate TLS at the reverse proxy and forward the original scheme/host. Preserve WebSocket upgrade support only when the optional Interactive Server islands are enabled. Cache static assets and immutable generated documentation aggressively; do not cache authenticated assistant responses.

## Container lifecycle

The deployment image must run as a non-root user, expose the configured ASP.NET Core port, use bounded CPU/memory limits, and stop gracefully. Readiness depends on the content index. Assistant/gateway readiness is independent and must not make static routes fail.

## Rollback

Publish immutable application image tags. Roll back by selecting the previous image tag and verifying `/health/ready`, `/docs`, sitemap output and the content version label. Do not run database migrations as part of portal startup.

**Status:** Deployment container, reverse-proxy smoke, resource-limit test and rollback rehearsal are NOT VERIFIED.
