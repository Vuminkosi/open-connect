# Migration: net8.0 → net10.0

> STUB (CF-IMP-017). This page expands once CF-IMP-003 lands the
> multi-target layout and CI records per-TFM evidence. Do not treat the
> checklist below as verified until the linked evidence exists.

## Planned steps

1. Retarget application and library TFMs from `net8.0` to `net10.0`
   (keep multi-targeting `net8.0;net9.0;net10.0` in libraries while net8
   remains compatibility-supported per `runtime-support-policy.md`).
2. Update `global.json` SDK roll-forward only if the pinned SDK cannot
   build net10.0 assets; keep the stable-SDK / net10-SDK isolation used in
   `cross-framework-ci.yml`.
3. Re-resolve EF Core / MAF dependency groups per TFM
   (`verify-nuget-dependency-groups.py` must stay green).
4. Re-run the full `cross-framework-ci.yml` matrix plus the
   `cross-framework-integration.yml` provider suite.
5. Regenerate `docs/compatibility/framework-matrix.md` via
   `render-compatibility-matrix.py` from recorded manifest evidence.

## Watch areas (to confirm with CF-IMP-003)

- EF Core major bump (8 → 10) behavioural deltas in migrations and
  provider SQL.
- MAF (`Microsoft.Agents.AI*`) API surface changes across versions.
- NATS client compatibility across runtimes.
- Blazor / browser-test baseline differences on the new runtime.

## Verification checklist

- [ ] `package-inspect` job green on all three TFMs.
- [ ] `consumer-smoke` passes for a net10.0 external consumer.
- [ ] Provider integration (PostgreSQL, SQL Server, SQLite, NATS) green on net10.0.
- [ ] Privacy fail-closed suite green on net10.0.
- [ ] Framework matrix regenerated with `pass` evidence, no `pending` cells
      for shipped packages.
