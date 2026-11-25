# OpenConnect Mobile (.NET MAUI Blazor Hybrid Sample)

Universal companion app for OpenConnect IMessaging. Connects to any OpenConnect-enabled company gateway via single-use desktop QR enrollment with strict multi-company profile isolation.

## Key Security Properties

- **Zero hardcoded secrets or company URLs**: All gateway URLs, tenant IDs, and company metadata come from authenticated QR enrollment + `GET /openconnect/v1/messaging/compatibility`.
- **Single active profile**: Supports multiple enrolled companies with strictly isolated credentials (`MAUI SecureStorage` namespaced by `profileId`), AES-256-GCM encrypted local cache, and monotonic profile-epoch fencing.
- **No-secret push-off fallback**: Builds and runs cleanly without `google-services.json` or Firebase server credentials; direct realtime messaging continues while push notifications honestly report disabled.
- **Explicit consent for calls**: Incoming calls never auto-answer and never activate the microphone prior to user acceptance.

## Build Commands

This sample is intentionally excluded from `OpenConnect.slnx` so non-MAUI Linux CI lanes are unaffected.

```bash
# Android (P0 target)
dotnet build samples/Mobile/OpenConnect.Samples.Mobile.csproj -f net10.0-android

# Windows (MAUI desktop target)
dotnet build samples/Mobile/OpenConnect.Samples.Mobile.csproj -f net10.0-windows10.0.19041.0
```
