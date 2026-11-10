# Worker T0–T4 Matrix (DRAFT)

**DRAFT — not final. Publish + live consumer pending. Gate 7 HARD_BLOCKED.**

Parent: [G9 reconciliation draft](../g9-reconciliation-draft.md). Worker tier tallies (T0 11/11, T1 11/11) were supplied with the assignment; no per-test log or mapping was confirmed by inspection. `workers/` directories observed 2026-11-10: `classification`, `common`, `inference`, `kokoro`, `mms-tts`, `navigator-playwright`, `nllb`, `operational-agents`, `outbox`, `realtime-voice`, `security`, `transcription` (+ `.gitkeep`). No tier-to-directory mapping or runner command was confirmed — commands below are placeholders (unexecuted examples), not verified procedure.

| Requirement / Acceptance | Test ID | Commit SHA | Command | SDK / Runtime / TFM + MAF | Model / Worker / DB | Result | Evidence pointer | Notes |
|---|---|---|---|---|---|---|---|---|
| Worker tier T0 — all supplied tests pass (11/11) | T0-01 … T0-11 (IDs supplied, not confirmed) | n/a | Tier runner command unconfirmed (unexecuted) | SDK/TFM unconfirmed | Worker/model/DB per test unconfirmed | NOT VERIFIED | No log inspected | Tally 11/11 supplied only; needs per-test log pointers + fixture IDs |
| Worker tier T1 — all supplied tests pass (11/11) | T1-01 … T1-11 (IDs supplied, not confirmed) | n/a | Tier runner command unconfirmed (unexecuted) | SDK/TFM unconfirmed | Worker/model/DB per test unconfirmed | NOT VERIFIED | No log inspected | Tally 11/11 supplied only; needs per-test log pointers + fixture IDs |
| Worker tier T2 | T2-xx (no IDs supplied) | n/a | n/a (not run) | n/a | n/a | NOT RUN | Assignment record only | Must remain NOT RUN; do not backfill |
| Worker tier T3 | T3-xx (no IDs supplied) | n/a | n/a (not run) | n/a | n/a | NOT RUN | Assignment record only | Must remain NOT RUN; do not backfill |
| Worker tier T4 | T4-xx (no IDs supplied) | n/a | n/a (blocked) | n/a | n/a | BLOCKED | Assignment record only | Blocker reason to be recorded by owning lane; no workaround claimed |

Fake-model vs real-model rule: if any T0/T1 test used a fake or stub model/worker, its PASS (once evidenced) proves harness behavior only — never cite it as real-model evidence. Real-model claims require the exact model ID, worker revision, and dataset/fixture pointer in the Model/Worker/DB column plus a provider-backed log.

Gap summary: per-test IDs, runner commands, SDK/TFM legs, model/worker/DB fixtures, and all T0/T1 logs are missing. T2/T3 must stay NOT RUN and T4 must stay BLOCKED until the owning lane supplies evidence or an approval ref.
