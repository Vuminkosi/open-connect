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

For local HTTPS, export a trusted localhost development certificate and its key into a protected directory outside the repository, then initialize the dedicated TLS volume once:

```powershell
$certificateDirectory = '<private-certificate-directory>'
dotnet dev-certs https --check --trust
dotnet dev-certs https --export-path (Join-Path $certificateDirectory 'localhost.pem') --format PEM --no-password
./scripts/setup-cloud-local-https.ps1 -CertificateDirectory $certificateDirectory
docker compose -f docker-compose-all.yml -f docker-compose-cloud-local.yml up -d --no-deps documentation-site
```

The HTTPS site is `https://localhost:5051` by default; `OPENCONNECT_DOCS_HTTPS_PORT` changes the port. Register the corresponding `/cloud/signin-google` or `/cloud/signin-oidc` callback with the configured provider. The initializer refuses to overwrite an existing certificate or key. Protect the exported key and the Docker TLS volume; they contain private certificate material.

For local development without a Google OAuth client, the optional `docker-compose-cloud-local-identity.yml` overlay supplies a development-only Keycloak OIDC provider. It requires the HTTPS override above and uses fixed `https://localhost:5051` and `https://localhost:9443` origins. Set `OC_LOCAL_OIDC_ADMIN_USERNAME`, `OC_LOCAL_OIDC_ADMIN_PASSWORD`, `OC_LOCAL_OIDC_USERNAME`, `OC_LOCAL_OIDC_PASSWORD`, and a long random `OC_LOCAL_OIDC_CLIENT_SECRET` in a protected environment. Do not use production credentials. Build the site and gateway images, then initialize the separate TLS volumes and start the provider with the site:

```powershell
./scripts/setup-cloud-local-identity-tls.ps1 -CertificateDirectory $certificateDirectory
docker compose -f docker-compose-all.yml -f docker-compose-cloud-local.yml -f docker-compose-cloud-local-identity.yml up -d documentation-site local-identity local-identity-loopback
```

The provider uses a persistent development database and imports its realm only on first startup. Changing environment credentials does not update an existing realm. Protect the identity data, certificate and key volumes. This provider is for local development only; it is not a substitute for a deployed identity provider or verification of Google sign-in. A separately migrated Cloud control-plane database is still required for organizations and applications.

Generate the API reference before building the image when that reference is needed. Generated artifacts present under the site directory are included in the image; a clean checkout without generated artifacts still serves the static documentation.

The optional outbox and operational workers require the OpenConnect PostgreSQL schema before startup. For the root Compose database, restore the pinned tools and dependencies, start PostgreSQL, and apply the reviewed sample migrations explicitly:

```powershell
dotnet tool restore
dotnet restore samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj --locked-mode
docker compose -f docker-compose-all.yml up -d postgres
./scripts/migrate-local-worker-database.ps1
docker compose -f docker-compose-all.yml --profile operational up -d worker-outbox worker-operational
```

The helper uses the running Compose PostgreSQL service's configured user and database. It renders idempotent SQL from the sample migrations; workers do not run schema changes. Back up existing data and review migration compatibility before applying schema changes to a database containing data. This schema is separate from the Cloud control-plane migrations described below.

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
