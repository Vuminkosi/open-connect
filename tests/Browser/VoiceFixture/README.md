# Deterministic local-provider voice smoke

This separate lane exercises the **real Blazor sample** with a disposable SQLite
file and synthetic transcription/inference/TTS endpoints. It leaves the existing
WAV/live-provider specs untouched. It does not establish speech recognition
quality, microphone permission behavior, live inference, production auth, or
physical-device behavior.

## Run on an authorized browser-capable host

Use the repository's pinned .NET SDK and existing `tests/Browser/package-lock.json`
(Playwright 1.65.0). Build the real sample from the same commit under test first:

```sh
dotnet restore samples/Blazor/OpenConnect.Samples.Blazor.csproj --locked-mode
dotnet build samples/Blazor/OpenConnect.Samples.Blazor.csproj -c Release --no-restore -warnaserror
dotnet restore tests/Browser/VoiceFixture/VoiceFixture.csproj --locked-mode --configfile tests/Browser/VoiceFixture/NuGet.Config
dotnet build tests/Browser/VoiceFixture/VoiceFixture.csproj -c Release --no-restore
dotnet tests/Browser/VoiceFixture/bin/Release/net10.0/VoiceFixture.dll --self-test
node --test tests/Browser/VoiceFixture/local-network.test.js
cd tests/Browser
npm ci
npx --no-install playwright install --with-deps chromium
npx --no-install playwright test --config=playwright.voice-local.config.js
```

Dependency/browser preparation is an explicit host setup step, not performed by
the fixture. The framework-only fixture has no NuGet package references; its own
NuGet configuration disables package sources and its lockfile is committed.
Neither the fixture nor the browser suite calls a real provider or downloads a
model. No real credentials are needed. The app's existing local-acceptance guard
uses ephemeral random test entropy; no user account authentication is configured.

The dedicated config uses exclusive `127.0.0.1:5187` (sample) and `:5188` (fake
providers). A collision fails, and an existing server is never reused. The fixture
creates a new temporary directory/database and a unique exact Todo title each
run, terminates the sample, then removes that directory on normal shutdown.
Abrupt process/host termination can leave the temporary directory behind; do not
reuse it. The child environment is cleared before known test-only configuration
is supplied. Every provider/service URL is explicit loopback, with no inherited
proxy or provider credentials. The fixture does not forward requests and denies
unknown routes. Playwright denies external HTTP **and WebSocket** destinations
before navigation and blocks service workers. The sample's one exact Google Fonts stylesheet URL is fulfilled with empty local CSS and recorded separately; it never goes upstream. Every other outward request fails the test. This is application/test-level
isolation, **not an OS network sandbox**; run only the reviewed sample sources.

The test requires zero exact-title matches before voice, a raw authoritative
transcript on the real browser socket, the exact normalized user message at the
fake model, a real authorized Todo tool result and exactly one record after
provider-close fallback. It reloads the real UI and checks the persisted title
and record count again. The fallback duplicate observation is bounded (one
second after Ready/reload), not a claim about indefinite retries. The fake model
returns tool calls only for this run's normalized message and an actually
advertised creation tool; it never writes Todo records itself.

## Evidence limits

Fixture transport self-test/build and URL-policy tests are not browser evidence.
Until the dedicated Playwright test runs, mark the UI/voice/persistence journey
**NOT VERIFIED**. The Documentation Portal workflow contains the intended hosted
lane after its real sample build; an earlier job failure can prevent that lane
from running. Do not describe an unexecuted step as passing. No existing live or
manual acceptance requirement is removed.

Only the dedicated allowlisted voice diagnostics JSON and persisted-UI screenshot
are attached on success. A separate bounded readiness-status JSON is attached
on early UI failure or success. The same readiness payload is printed as one
`OPENCONNECT_VOICE_READINESS` line in the hosted job log. It contains stage
labels, counts and booleans, never circuit URLs, query tokens, headers or raw
errors. Raw Playwright traces, automatic screenshots, video, request headers,
guard tokens, full provider/tool payloads and database/storage files are not
retained. No pinned upload-artifact usage exists in the repository workflows,
so this lane does not add an unverified uploader: hosted artifact retention is
NOT CONFIGURED. Collect only these dedicated sanitized attachments on an authorized
host; do not upload the whole Playwright output directory or app storage.
