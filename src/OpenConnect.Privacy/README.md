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

## License

Same terms as the OpenConnect repository.
