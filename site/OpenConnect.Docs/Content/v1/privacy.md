---
title: Privacy firewall
version: v1
summary: Keep privacy and authorization at the trusted .NET host boundary.
order: 20
---

# Privacy firewall

The host privacy firewall is enabled by default. Worker detectors return signals; the .NET host makes the final policy decision.

## Fail closed

When a configured protected egress requires unavailable detector coverage, the host blocks that egress. Optional detector absence must not be advertised as ready protection.

## Boundary rules

Raw sensitive content must not enter NATS messages, telemetry or unauthorized tools. The token vault scope contains the tenant, principal, session and purpose. Applications that require turn- or policy-revision-specific restoration must enforce those additional restrictions in their host authorization and restoration policy.

## Evaluate before egress

### Gate an external model request

Resolve `IPrivacyFirewall` from the host's service provider and use the authenticated tenant, principal and session to construct the context. This fragment shows the decision boundary; `content` and the IDs come from the trusted application request context:

```csharp
using OpenConnect.Privacy;

var context = new EgressContext(tenantId, principalId, sessionId,
    EgressDestination.ExternalModel);
var decision = await firewall.EvaluateAsync(content, context, cancellationToken);
if (!decision.Allowed || decision.SanitizedContent is null)
{
    throw new PrivacyBlockedException("The request cannot leave the host.");
}

// Send decision.SanitizedContent through the authorized destination only.
```

Never fall back to the original content after a denial or detector failure. Re-evaluate for the actual destination: model, tool, worker and application output can have different policies. Authentication and authorization are separate checks; the firewall does not establish a caller's identity. Inspect model and tool output before displaying or forwarding it.

## Configure and operate

### Enable the host privacy boundary

```csharp
using OpenConnect.Core;

builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true;
});
```

Configure the host's privacy policy and detector services separately. Do not disable output inspection to work around a denied request.

`PrivacyOptions` defaults to enabled, `StrictLocal`, required egress validation, tokenization and output inspection, with a 30-minute default token lifetime. A disabled optional model detector does not imply that it is available. Configure only detectors you have installed and can operate; a required unavailable detector blocks the protected path. A detector result is a signal, not a guarantee that every sensitive value was found.

Token vault operations use `PrivacyScope` and a time-to-live. Keep encryption keys and backing storage under the host's control, restrict access and plan for expiry. Protect sanitized text, vectors, source documents and audit records according to their actual sensitivity. Avoid including source content in error messages or logs. See [troubleshooting](/docs/v1/troubleshooting) when an egress is denied.

```csharp
// vault is an ITokenVault from the host; IDs are from the authenticated context.
var scope = new PrivacyScope(tenantId, principalId, sessionId, "support-case");
string token = await vault.StoreAsync(
    sensitiveValue, "account", scope, TimeSpan.FromMinutes(10), cancellationToken);

// Resolve only in the same authorized scope, before expiry.
string? value = await vault.ResolveAsync(token, scope, cancellationToken);
```

Treat `token` and the restored value as sensitive. A missing or expired token resolves to `null`.

## Related links

- [Tools, approvals and MCP](/docs/v1/tools-and-mcp)
- [Documents, knowledge and vision](/docs/v1/documents-and-knowledge)
