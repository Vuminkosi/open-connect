# OpenConnect.Docs

The official OpenConnect developer portal is an ASP.NET Core .NET 10 Blazor Web App.

- Static SSR is the default.
- Interactive Server is limited to search and theme controls.
- Documentation content is versioned Markdown under `Content/<version>/`.
- `/health/live`, `/health/ready` and `/sitemap.xml` are available without AI infrastructure.
- The optional OpenConnect assistant is disabled unless explicitly configured.
- The protected Cloud workspace is additive; public docs do not depend on Cloud identity or PostgreSQL.

Build with:

```powershell
dotnet restore site/OpenConnect.Docs/OpenConnect.Docs.csproj --locked-mode
dotnet build site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --no-restore -warnaserror
```

## API generation

From the repository root, after a serialized Release build:

```powershell
./scripts/generate-docs-api.ps1 -Mode all
```

The pinned DocFX tool consumes the actual source projects and emits versioned API metadata plus copied OpenAPI and JSON Schema contracts under `site/OpenConnect.Docs/generated/`. Generated output is ignored and must be rebuilt in CI.

## Cloud workspace configuration

Cloud identity and its PostgreSQL control plane are optional. To enable Cloud sign-in, configure an external OIDC provider and supply these deployment settings through the hosting environment or secret store:

```text
OpenConnect__Cloud__Identity__Enabled=true
OpenConnect__Cloud__Identity__Authority=<OIDC authority URL>
OpenConnect__Cloud__Identity__ClientId=<OIDC client ID>
OpenConnect__Cloud__Identity__ClientSecret=<OIDC client secret>
ConnectionStrings__CloudControlPlane=<PostgreSQL connection string>
```

When identity is enabled, all four identity settings are required and incomplete configuration stops startup. The client secret must not be committed or sent to browser code. Without the PostgreSQL connection string, Cloud identity can still authenticate, but organization and application APIs report that the control plane is unavailable. Public documentation and its readiness endpoint do not connect to the Cloud database. Cloud schema migrations are explicit and are not applied automatically during portal startup.

Cloud API writes use the authenticated session and antiforgery validation. A same-origin client first fetches `GET /cloud/api/v1/antiforgery`, keeps the antiforgery cookie, and sends the returned token in the `X-CSRF-TOKEN` header for each state-changing request. `POST /cloud/sign-out` uses a standard antiforgery form.

The current control plane supports organization creation, membership-based organization listing, application registration/listing, logical service catalog, tenant/application capability selection, revisioned selection APIs, and scoped machine-credential issuance/list/rotation/revocation. The creator becomes the organization owner. Only owners and administrators can register applications, change service selections, or manage credentials. Initial capability definitions report platform availability as `not_configured`; region and modality support are marked unverified. A selection or authenticated credential never permits execution by itself. Subscription grants, gateway authorization/routing, usage, and firewall receipts remain unimplemented and are not represented as operational.

The authenticated catalog API is `GET /cloud/api/v1/capabilities`; tenant and application selections are listed under their `/capabilities` resources. Selection writes require `If-Match: "<revision>"`, with `"0"` when no selection exists. The API rejects selection when entitlement, platform availability, parent selection, or dependencies do not permit it. These APIs do not invoke workers.

Machine credentials are created through the application `credentials` management endpoint and are returned once in a `no-store` response. Only the digest and a non-secret fingerprint are persisted. Callers must put the value in their server-side secret store immediately; the portal does not place it in JS/static assets. Credential scopes must reference capabilities that are currently selectable for the application. Credential validation alone does not authorize cloud execution because the Cloud gateway admission path is not yet implemented.

The EF migration tool is pinned in `dotnet-tools.json`. Run it with a deployment-approved database and the same connection string setting, for example:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project site/OpenConnect.Docs/OpenConnect.Docs.csproj --startup-project site/OpenConnect.Docs/OpenConnect.Docs.csproj --configuration Release
```
