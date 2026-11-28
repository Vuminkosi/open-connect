# OpenConnect.Privacy

Privacy firewall for OpenConnect: policy engine, tokenization, and egress gates
(`OpenConnect.Privacy`, `OpenConnect.Privacy.Policy`, `OpenConnect.Privacy.Tokenization`,
`OpenConnect.Privacy.Gates`) that deny by default and fail closed.

## Install

```bash
dotnet add package OpenConnect.Privacy
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

var firewall = provider.GetRequiredService<IPrivacyFirewall>();
EgressDecision decision = await firewall.EvaluateAsync(
    content,
    new EgressContext(
        new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()),
        EgressDestination.Output));
```

`IPrivacyFirewall.EvaluateAsync` (`PrivacyContracts.cs:99`) applies the policy engine
(`PrivacyFirewall.cs:28`) and returns an `EgressDecision` (`PrivacyContracts.cs:71`)
scoped by `EgressContext` (`:64`). Deny-by-default: disabled detectors fail closed on
external egress.

## Token restoration and secrets

Tokens of secret categories (`SECRET`, `CREDENTIAL`, `API_KEY`, `PRIVATE_KEY`,
`ACCESS_TOKEN`, `PASSWORD`, see `TextOverlapResolver.IsSecretCategory`) are never
restored to plaintext. `TokenRestorationService` (`FinalUserOutput` and
`ToolArguments`) and `StreamingPlaceholderBuffer` apply the same rule and return
identical output for the same text and scope.

Restoration is fail closed. A token is resolved only when its vault implements
`ISecretAwareTokenVault` and reports the token as not secret. A vault that only
implements `ITokenVault` (including a decorator around `TokenVault`), or whose
`IsSecretToken` check throws, never has tokens restored; the placeholder stays in
the output. Custom or decorated vaults must implement `ISecretAwareTokenVault`
and delegate `IsSecretToken` to report categories. The
`StreamingPlaceholderBuffer(Func<string, ValueTask<string?>>)` constructor has no
vault, so the delegate owns the decision and must not return secret plaintext.

## Host evidence reporting

Host reporting is opt-in. Configure a dedicated client certificate and active
Cloud application credential from a secret store, then register the sink and
recording firewall before resolving the host gate:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

// cloudAuthority, clientCertificate and applicationCredential come from the host's secure configuration.
services.AddOpenConnectRulesDetector();
services.EnsureHostPrivacyServices();
services.AddSingleton<IHostPrivacyEvidenceSink>(_ =>
    new CloudPrivacyEvidenceSink(cloudAuthority, clientCertificate, applicationCredential));
services.AddRecordedPrivacyFirewall(provider => new PrivacyFirewall(
    provider.GetRequiredService<IOptions<PrivacyOptions>>(),
    provider.GetRequiredService<PolicyEngine>()));
```

The Cloud service must enable `OpenConnect:Cloud:HostPrivacyTrust:Enabled` and
configure `OpenConnect:Cloud:HostPrivacyTrust:ClientCertificateSha256` with the
dedicated certificate's SHA-256 fingerprint. Startup rejects host-reporting
pins that overlap gateway or worker client pins. Configure HTTPS, a migrated Cloud database and an
active application credential for the same tenant. Convert the Cloud tenant
GUID with `CloudTenantIdentity.ToHostTenantId` when constructing host egress
contexts; independently assigned host tenant IDs are not accepted by this
receiver.

Structured-content inspection checks raw JSON, decoded fields, labels paired
with values, and a bounded concatenation of values to catch some identifiers
split across fields. The combined check can conservatively block unrelated
adjacent values that resemble sensitive data. It blocks when its 50,000-fragment
or 1,000,000-character combined-value limit is exceeded. It does not guarantee
identification of all sensitive information.

Each reported decision contains an event ID, tenant, destination, allowed state,
policy action, an allowlisted reason category and category counts, but no source
content, raw detector message or entity offsets. Existing reports without a
reason remain marked as not recorded. Counts are detector observations rather than a complete inventory, and
the recorded decision does not prove a subsequent operation occurred. The
chat boundary guard records a separate blocked decision when it rejects an
entity split across messages; its earlier inspection decision may also be
recorded, so totals count decisions rather than unique requests or values. The
chat guard also checks combined text and structured tool content across messages
before dispatch and after receiving a response. This check can conservatively
block unrelated adjacent values that resemble sensitive data. The
sink waits for Cloud to acknowledge the exact event before returning; reporting
failures block the guarded operation. The sink retries transient connection errors
and selected temporary HTTP failures at most twice with the same event ID, within
one deadline. It has no local durable outbox. Applications should account for Cloud availability and should not treat
this opt-in integration as a guarantee of complete PII detection. Deploy the
updated Cloud receiver and its database migration before enabling a sender that
includes reason codes; an older receiver rejects the additional field.

## License

Same terms as the OpenConnect repository.
