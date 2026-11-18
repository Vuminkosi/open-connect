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

## License

Same terms as the OpenConnect repository.
