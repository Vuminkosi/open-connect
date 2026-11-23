# OpenConnect.Navigator.OperationalBrowser

Isolated browser execution for operational jobs: per-job browser sessions behind a
Playwright tool allowlist and an egress guard (`OpenConnect.Navigator.OperationalBrowser`,
`OpenConnect.Navigator.Playwright`).

## Install

```bash
dotnet add package OpenConnect.Navigator.OperationalBrowser
```

Ships `net8.0`, `net9.0`, and `net10.0` assets; `net10.0` is the default for new consumers.

## How to use

```csharp
using OpenConnect.Navigator.OperationalBrowser;

(bool allowed, string reason) = await boundary.ValidateAsync(
    job, destination, step: 0, bytes: 0);
if (!allowed)
{
    return Results.Forbid();
}

bool egressOk = await egress.IsAllowedAsync(destination, job.AllowedOrigins);
```

`BrowserJobBoundary.ValidateAsync` (`BrowserJobBoundary.cs:9`) enforces job budgets and
the Playwright tool allowlist; `EgressGuard.IsAllowedAsync` (`EgressGuard.cs:22`)
revalidates each HTTPS destination against the allowed origins. Pair with
`OpenConnect.Navigator.Operational` for durable scheduling.

The guard denies every resolved address that is not globally routable: private, loopback,
link-local, shared, documentation, benchmarking, reserved and multicast IPv4 ranges, and the
IPv6 equivalents plus Teredo, local-use NAT64 and IPv4-compatible forms. IPv4 embedded in
IPv4-mapped, NAT64 (`64:ff9b::/96`) and 6to4 (`2002::/16`) addresses is checked with the IPv4
rules. All records returned for a host must be public.

## License

Same terms as the OpenConnect repository.
