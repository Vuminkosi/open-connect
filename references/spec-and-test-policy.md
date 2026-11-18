# Adaptive spec and test policy

T0: localized nonbreaking fix, problem, acceptance, touched paths, tests.
T1: multi-file SDK defect or small feature: requirements, design, tasks, package/API/consumer tests, error modes.
T2: privacy, tenant boundaries, public API, persistence, transport, cross-framework changes: full threat/compatibility analysis, schema/version plan, rollback and negative tests.
Author implementation must never be its own independent final approval. Verify exact final SHA/diff. Focus on .NET locked restore/build/test; Python uv locked/ruff/mypy/pytest; npm ci/lint/typecheck/vitest/build; contract fixtures; package consumption; cross-TFM; no unapproved downloads, production tests or paid resources. Report PASS, FAIL, NOT VERIFIED, NOT APPLICABLE accurately. Treat code changes affecting privacy authorization as security-gated.
