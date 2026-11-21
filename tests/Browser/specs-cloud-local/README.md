# Local Cloud browser acceptance

The management metering check uses a running local HTTPS portal, local OIDC provider, and PostgreSQL container. It signs in and reads ledger aggregates; it does not create credentials or submit capability requests. Run it against an isolated test tenant without concurrent capability requests, since concurrent usage can change the same aggregates.

Set `OPENCONNECT_CLOUD_MANAGEMENT_ACCEPTANCE_ENABLED=1`, `OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL` to the loopback HTTPS origin, and `OPENCONNECT_CLOUD_ACCEPTANCE_TENANT` to the test tenant GUID. Supply `OC_LOCAL_OIDC_USERNAME` and `OC_LOCAL_OIDC_PASSWORD` through a protected environment. Set `OPENCONNECT_CLOUD_TEST_DB_CONTAINER`, `OPENCONNECT_CLOUD_TEST_DB_USER`, and `OPENCONNECT_CLOUD_TEST_DB_NAME` for a database account that can read the Cloud ledger tables. The container needs `psql` and local Docker access. Do not commit credentials.

From `tests/Browser`, run:

```powershell
npx playwright test --config=playwright.cloud-local-voice.config.js management-metering.spec.js
```

The test is skipped unless explicitly enabled and configured. It compares usage and reservation counts and totals for the selected tenant, plus execution and LiveUI consumption counts. It covers the specific pages visited; it is not evidence for all management operations or billing paths. The suite disables traces, screenshots and video because it handles credentials.

## Documentation speech output

With the documentation assistant, model provider and Kokoro worker configured, set `OPENCONNECT_SITE_SPEECH_ACCEPTANCE_ENABLED=1` and `OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL` to the loopback HTTPS site origin. Run:

```powershell
npx playwright test --config=playwright.cloud-local-voice.config.js site-speech-output.spec.js
```

The test submits a short text request and observes a non-silent audio buffer start and finish in the browser. It permits autoplay for automation and does not verify audible output on a physical device or microphone transcription.

## Unavailable transcription recovery

When testing an intentionally unavailable local transcription worker or model, set `OPENCONNECT_SITE_VOICE_UNAVAILABLE_ACCEPTANCE_ENABLED=1` and `OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL` to the loopback HTTPS site origin. Run:

```powershell
npx playwright test --config=playwright.cloud-local-voice.config.js site-voice-unavailable.spec.js
```

With local voice-readiness integration enabled, the test expects the microphone to be disabled before capture and verifies that a subsequent text-driven LiveUI search succeeds. It does not verify successful transcription or a physical microphone.

## Cloud gateway voice synthesis

To exercise the complete local Cloud capability path, set `OPENCONNECT_CLOUD_ACCEPTANCE_ENABLED=1`, `OPENCONNECT_CLOUD_ACCEPTANCE_PORTAL` to the loopback HTTPS portal, `OPENCONNECT_CLOUD_ACCEPTANCE_GATEWAY` to the loopback HTTP gateway, and `OPENCONNECT_CLOUD_ACCEPTANCE_TENANT` and `OPENCONNECT_CLOUD_ACCEPTANCE_APPLICATION` to an authorized test scope. Supply `OC_LOCAL_OIDC_USERNAME` and `OC_LOCAL_OIDC_PASSWORD` through a protected environment. The selected tenant must have a ready and entitled voice deployment and quota.

```powershell
npx playwright test --config=playwright.cloud-local-voice.config.js voice-journey.spec.js
```

This test creates and revokes a short-lived application credential, submits a synthetic synthesis request, and checks the resulting usage and privacy records. It creates a real local ledger entry and audit records; run it only in an appropriate test environment. It verifies generated PCM, not playback on a physical device or microphone transcription.
