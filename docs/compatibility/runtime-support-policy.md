# Runtime Support Policy

Verdict first: **net10.0 is the recommended production target.**
net8.0 and net9.0 remain **compatibility-only** after the November 2026
.NET 8 end-of-support (EOS); they keep building and testing in CI so
existing consumers can migrate on a planned path, but new production
deployments should target net10.0.

## Support windows (Microsoft)

| Runtime | Release type | Support ends | Status after EOS in this repo |
|---|---|---|---|
| .NET 8 | LTS | **10 November 2026** | Compatibility-only (build + test, no new features gated on it) |
| .NET 9 | STS | **12 May 2026** | Compatibility-only (already past EOS at time of writing) |
| .NET 10 | LTS | November 2028 (3 years from Nov 2025 GA) | **Recommended for production** |

Authoritative references (Microsoft):

- .NET Support Policy: <https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core>
- .NET 8 lifecycle (LTS, ends Nov 10 2026): <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>
- .NET 9 lifecycle (STS, ended May 12 2026): <https://dotnet.microsoft.com/en-us/download/dotnet/9.0>
- .NET 10 lifecycle (LTS): <https://dotnet.microsoft.com/en-us/download/dotnet/10.0>

Verify the dates above against those pages before each release; Microsoft
occasionally adjusts servicing windows.

## What compatibility-only means

- CI (`cross-framework-ci.yml`) continues to restore, compile, unit-test,
  provider-test, package-inspect and consumer-smoke all three TFMs.
- Security and correctness fixes are back-portable to the net8.0/net9.0
  assets while they ship in the same package.
- New framework-dependent capabilities may require net9.0/net10.0; such
  gating must be documented in the package notes and the framework matrix,
  never silent.
- After November 2026, net8.0/net9.0 issues that stem from the unsupported
  runtime itself (rather than OpenConnect code) are documented as
  environment limitations with a migrate-to-net10.0 recommendation.

## Migration

Planned path: net8.0 → net10.0. See
`docs/compatibility/migration-net8-to-net10.md` (stub; expands with
CF-IMP-003 evidence).
