---
title: Cloud workspace and capabilities
version: v1
summary: Configure the optional Cloud control plane and understand capability admission.
order: 85
---

# Cloud workspace and capabilities

The optional Cloud workspace manages organizations, applications, capability selections and machine credentials. Its control-plane APIs and capability gateway have different contracts from the application gateway at `/openconnect/v1`; do not point the standard composer client at the Cloud capability gateway. A configured Cloud workspace does not mean that every catalog service is deployed or operational.

## Configure the workspace

The site can serve public documentation without Cloud identity or its database. To enable Cloud identity, configure an OIDC provider, registered HTTPS callback and secret values through your deployment environment. The site supports settings including:

```text
OpenConnect__Cloud__Identity__Enabled=true
OpenConnect__Cloud__Identity__Authority=<identity-provider-authority>
OpenConnect__Cloud__Identity__ClientId=<client-id>
OpenConnect__Cloud__Identity__ClientSecret=<secret>
ConnectionStrings__CloudControlPlane=<postgresql-connection-string>
```

Apply reviewed Cloud migrations explicitly to the intended database; site startup does not apply them. The Cloud database and worker persistence migrations are separate. Back up existing data and test upgrades before applying changes. See `site/OpenConnect.Docs/README.md` in the source repository for Compose configuration and migration commands. Protect credentials and data-protection keys, and configure trusted HTTPS termination.

## Select a capability

1. Sign in and create or select an organization. The creator becomes its owner; application and credential management require the appropriate owner or administrator role.
2. Register an application and inspect the authenticated catalog at `GET /cloud/api/v1/capabilities`. Check entitlement, availability, region, deployment and any dependencies for the intended service.
3. Select permitted capabilities at organization and application scope. Selection writes use `If-Match: "<revision>"`; use `"0"` when no selection exists. On a conflict, read the latest selection and reconcile rather than blindly overwriting another administrator's change.
4. Create a scoped application machine credential. It is returned once in a `no-store` response: immediately store it in a server-side secret manager. Rotate or revoke it through the application credential endpoints when needed. Do not embed it in a browser bundle.
5. Configure the corresponding gateway and worker deployment, privacy policy and quotas, then verify a real authorized request. A selection and credential alone do not permit execution or prove readiness.

## API write protection

Cloud API writes use an authenticated session and antiforgery validation. A same-origin client obtains `GET /cloud/api/v1/antiforgery`, retains its cookie and includes the returned token in the `X-CSRF-TOKEN` header for state-changing requests. The catalog and selection APIs configure control-plane state; they do not themselves execute workers. Treat unconfigured or unverified availability as unavailable until the deployment and policy are established.

Public documentation and Cloud management visits are not capability execution requests. Usage is associated with admitted capability calls and their settlement, not with opening the site or dashboard. The usage view reports recorded quota ledger entries; it is not an invoice or a complete statement of provider charges.

```ts
// Run from the signed-in site's origin. Preserve the session and antiforgery cookies.
const tokenResponse = await fetch("/cloud/api/v1/antiforgery", {
  credentials: "same-origin",
});
if (!tokenResponse.ok) throw new Error("Cannot obtain antiforgery token");
const { requestToken } = await tokenResponse.json();

const response = await fetch("/cloud/api/v1/tenants", {
  method: "POST",
  credentials: "same-origin",
  headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": requestToken },
  body: JSON.stringify({ name: "Example organization" }),
});
if (!response.ok) throw new Error(`Organization creation failed: ${response.status}`);
const organization = await response.json();
```

## Related links

- [Supported services and integrations](/docs/v1/supported-services)
- [Privacy firewall](/docs/v1/privacy)
