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

The root Compose file also runs the documentation site on `http://localhost:5050` by default:

```powershell
docker compose -f docker-compose-all.yml up -d --build documentation-site
```

Set `OPENCONNECT_DOCS_PORT` to change the loopback port. Public documentation is available without the Cloud database or identity providers. The dedicated `oc_docs_keys` volume preserves server-side authentication protection keys across container replacement; protect and back up that volume when identity is enabled. Cloud sign-in requires a configured provider, a registered callback and an HTTPS deployment or trusted HTTPS termination; it is not enabled by the local HTTP default.

For Compose, `CLOUD_OIDC_ENABLED`, `CLOUD_OIDC_AUTHORITY`, `CLOUD_OIDC_CLIENT_ID` and `CLOUD_OIDC_CLIENT_SECRET` configure an OIDC provider. `CLOUD_GOOGLE_ENABLED`, `CLOUD_GOOGLE_CLIENT_ID` and `CLOUD_GOOGLE_CLIENT_SECRET` configure Google sign-in. `CLOUD_CONTROL_PLANE_CONNECTION` configures the separate Cloud PostgreSQL schema. Supply credentials using deployment secrets or a protected environment, and apply Cloud migrations before enabling the control plane.

Generate the API reference before building the image when that reference is needed. Generated artifacts present under the site directory are included in the image; a clean checkout without generated artifacts still serves the static documentation.

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

The control plane supports organization creation, membership-based organization listing, application registration/listing, logical service catalog, tenant/application capability selection, revisioned selection APIs, and scoped machine-credential issuance/list/rotation/revocation. The creator becomes the organization owner. Only owners and administrators can register applications, change service selections, or manage credentials. Initial capability definitions report platform availability as `not_configured`; region and modality support are marked unverified. A selection or authenticated credential never permits execution by itself. Gateway routing and usage evidence exist for selected capabilities when their deployment, authorization and quota requirements are configured; this does not establish that all Cloud services or billing are operational.

The authenticated catalog API is `GET /cloud/api/v1/capabilities`; tenant and application selections are listed under their `/capabilities` resources. Selection writes require `If-Match: "<revision>"`, with `"0"` when no selection exists. The API rejects selection when entitlement, platform availability, parent selection, or dependencies do not permit it. These APIs do not invoke workers.

Machine credentials are created through the application `credentials` management endpoint and are returned once in a `no-store` response. Only the digest and a non-secret fingerprint are persisted. Callers must put the value in their server-side secret store immediately; the portal does not place it in JS/static assets. Credential scopes must reference capabilities that are currently selectable for the application. Credential validation alone does not authorize Cloud execution: gateway admission also checks capability, deployment, privacy and quota requirements.

The EF migration tool is pinned in `dotnet-tools.json`. Run it with a deployment-approved database and the same connection string setting, for example:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project site/OpenConnect.Docs/OpenConnect.Docs.csproj --startup-project site/OpenConnect.Docs/OpenConnect.Docs.csproj --configuration Release
```
