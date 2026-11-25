# ADR-IMSG-002: Device Enrollment, Desktop QR Pairing, and Mobile Identity

- **Status:** Accepted
- **Date:** 2026-11-25
- **Scope:** Desktop MiniComposer pairing, Gateway enrollment endpoints, Mobile MAUI client, `OpenConnect.Core`, `OpenConnect.Client`

## 1. Context

OpenConnect Mobile is a universal .NET MAUI Blazor Hybrid companion capable of connecting to multiple independent company gateways (one active profile at a time). Users sign in on a company web application (desktop MiniComposer) and link their mobile device via QR code.

A naive approach—embedding the desktop user's access token or refresh token inside the QR code—creates severe security risks:
- Shoulder-surfing or QR capture immediately grants reusable session credentials without confirmation.
- Desktop logout or session expiry becomes coupled to mobile sessions, or vice versa.
- No cryptographic binding to the mobile hardware/platform keystore.

## 2. Decision

### 2.1 Single-Use High-Entropy Enrollment Token (Never Desktop Credentials)

The QR payload (`contracts/jsonschema/imessaging-enrollment-qr-v1.json`) conveys:
- `version`: `1`
- `type`: `"openconnect.device.enrollment"`
- `baseGatewayUrl`: Canonical HTTPS gateway origin/base path (no embedded credentials, no fragments, no unexpected query params)
- `tenantId`: Opaque tenant identifier
- `applicationId`: Application identifier
- `pairingId`: Opaque pairing session identifier
- `enrollmentToken`: Single-use CSPRNG token with **at least 256 bits of entropy**
- `expiresAt`: UTC expiry timestamp (default **120 seconds** from creation)

The gateway persists **only a cryptographic hash** (e.g., SHA-256 with domain separation) of `enrollmentToken`, bound to `(tenantId, applicationId, issuer, subjectId, desktopSessionId)`. Plaintext tokens are never persisted or logged.

### 2.2 Explicit 4-Step Pairing State Machine

```text
Pending -> Claimed -> Approved -> Consumed
   |          |           |
   +--> Expired / Cancelled / Denied (terminal)
```

1. **Create (`POST /openconnect/v1/devices/enrollment`)**: Authenticated desktop principal requests pairing. Gateway checks `IDeviceEnrollmentAuthorizer` and recent-auth/MFA policy, generates a 256-bit CSPRNG token, stores its hash in state `Pending` with 120s TTL.
2. **Redeem / Claim (`POST /openconnect/v1/devices/enrollment/redeem`)**:
   - Mobile validates QR schema, HTTPS scheme, certificate, and host before sending any request; user explicitly confirms destination.
   - Mobile generates a new profile-scoped asymmetric device keypair in platform-protected storage (Android Keystore / iOS Keychain via MAUI SecureStorage).
   - Mobile submits `pairingId`, `enrollmentToken`, device public key, and a signed proof-of-possession challenge.
   - Gateway performs an **atomic single-winner CAS** (`Pending -> Claimed`), verifies hash/expiry/rate-limit/proof, and returns a short human-readable verification code (derived from pairing ID + device public key fingerprint). **No reusable credentials are issued yet.**
3. **Explicit Desktop Confirmation (`POST /openconnect/v1/devices/enrollment/{id}/confirm`)**:
   - The **original desktop principal** verifies the matching short code/fingerprint and explicitly approves the pending device (`Claimed -> Approved`).
   - Tenant or principal substitution is rejected; unapproved or cancelled pairings transition to terminal `Denied`/`Cancelled`.
4. **One-Time Completion (`POST /openconnect/v1/devices/enrollment/{id}/complete`)**:
   - Mobile submits fresh proof-of-possession for the claimed device key.
   - Gateway atomically transitions `Approved -> Consumed` (replays rejected with `409 Conflict` / `410 Gone`) and invokes `IMobileTokenIssuer` to issue:
     - Short-lived device-bound access token (audience/issuer/tenant/app/subject/device scoped; DPoP RFC 9449 proof where supported).
     - Bounded-lifetime rotating refresh grant (`RefreshGrant` entity storing hashed credential + rotation family ID).

### 2.3 Independent Lifecycle, Refresh Rotation, and Reuse Detection

- **Session Independence**: Once `Consumed`, the mobile session operates independently of desktop browser closure within host policy limits.
- **Strict Rotation & Reuse Detection**:
  - `POST /openconnect/v1/devices/token/refresh` rotates the refresh grant atomically within its `rotationFamilyId`.
  - Presenting an already-rotated (reused) refresh grant immediately **revokes the entire rotation family**, closes active device websockets, removes push registrations, and logs a security audit event.
  - Refresh lifetime is strictly bounded (no permanent tokens).
- **Per-Profile Isolation on Mobile**:
  - Profile identity tuple: `(canonicalGatewayOrigin, basePath, tenantId, applicationId, issuer, subjectId, deviceId)`.
  - Exactly one profile is active at a time; credentials, crypto state, local encrypted cache, push handles, and `HttpClient` scopes are strictly isolated per profile.
  - Profile switches increment a monotonic `profileEpoch`; late async callbacks with stale epochs are dropped.
- **Device Revocation (`DELETE /openconnect/v1/devices/{id}`)**:
  - Atomically marks the device revoked, revokes its refresh grant family, terminates active realtime websockets, deletes push registrations, emits `messaging.device.revoked`, and triggers conversation key epoch rekeying so the revoked device cannot decrypt subsequent messages.

## 3. Consequences

- Hosts that cannot issue independent device-scoped mobile credentials MUST leave device enrollment disabled and return an actionable configuration error rather than minting fake or over-privileged tokens.
- Native external OAuth flows, when configured by a host, use system browser Authorization Code + PKCE (RFC 8252); cookie scraping and embedded WebView login are prohibited.
