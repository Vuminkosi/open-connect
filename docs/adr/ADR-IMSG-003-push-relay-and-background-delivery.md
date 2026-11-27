# ADR-IMSG-003: Push Notifications, Opaque Wake-Up Allowlist, and Optional Shared Push-Relay Mode

- **Status:** Accepted
- **Date:** 2026-11-25
- **Scope:** `workers/imessaging` (`notifications/`, `api/`), `OpenConnect.IMessaging`, `samples/Mobile`, `contracts/jsonschema/imessaging-push-wakeup-v1.json`

## 1. Context

OpenConnect Mobile (Android P0) needs background wake-up notifications when new messages, call invitations, or approval requests arrive while the app is backgrounded or closed. Two structural constraints apply:

1. **Zero-Plaintext / Privacy-First**: Push providers (Google FCM, browser Web Push relays) are untrusted third-party delivery intermediaries. Sending message text, task titles, participant names, company URLs, or tokens through push payloads violates OpenConnect's privacy firewall and E2EE guarantees.
2. **Universal Binary Firebase Ownership**: A universal OpenConnect Mobile Android APK is compiled against a single app-owned Firebase project. Arbitrary self-hosted company gateways cannot send FCM messages directly to that universal APK's registration tokens using their own unrelated Firebase project credentials.

## 2. Decision

### 2.1 Push Is an Opaque Wake-Up Hint Only

- **Best-Effort Wake-Up**: FCM (HTTP v1 API with workload identity / service account credentials — **never** legacy server keys) and optional Web Push (VAPID) are strictly best-effort wake-up hints, never a source of truth and never a delivery/read acknowledgement.
- **Strict Payload Allowlist (`contracts/jsonschema/imessaging-push-wakeup-v1.json`)**:
  Every push payload MUST validate against `imessaging-push-wakeup-v1.json` with `additionalProperties: false` and may contain **only**:
  - `schemaVersion` (`1`)
  - `notificationId` (opaque correlation ID)
  - `profileHandle` (opaque locally mapped profile routing handle — never a gateway URL or tenant name)
  - `eventClass` (`"message" | "call" | "approval" | "system"`)
  - `displayTitle` (generic text, e.g., `"New message"`, max 64 chars)
  - `displayBody` (generic text, e.g., `"Open app to view"`, max 120 chars)
  - `ttlSeconds` (`1..86400`)
  - `collapseKey` (optional opaque wake-up dedup key)
- **Hard Rejection of Sensitive Fields**: `workers/imessaging` enforces both strict schema allowlisting (`extra="forbid"`) and content-sanitation checks that reject URLs (`http://`, `https://`), JWT/Bearer tokens, key material, email addresses, and excessive text in `displayTitle`/`displayBody`.
- **Authenticated Retrieval**: Upon waking or user tap, the mobile app maps `profileHandle` to its local profile (prompting an explicit profile switch if inactive) and fetches authoritative encrypted events over authenticated HTTPS/WSS from the company gateway.

### 2.2 Two Deployment Modes Inside `workers/imessaging` (Zero New SDK Projects)

`workers/imessaging` supports two push dispatch modes controlled by `OC_IMESSAGING_PUSH_MODE`:

1. **Direct Mode (`direct` — Company-Branded Builds / Self-Hosted FCM or Web Push)**:
   - Used when a company builds its own branded mobile binary with its own Firebase project, or uses Web Push/VAPID.
   - Company gateway sends signed `POST /internal/v1/execute` (`kind: "messaging.push.dispatch"`) to its private `workers/imessaging` instance.
   - Worker dispatches via FCM HTTP v1 (using server-side service account / workload identity credentials) or Web Push VAPID.
   - Push capability (`messaging.push.fcm`, `messaging.push.webpush`) is advertised in `GET /internal/v1/capabilities` **only** when valid provider credentials are configured and verified.
2. **Optional Shared Push-Relay Mode (`relay` — Universal Mobile Binary)**:
   - Operated by the universal app publisher using the same `workers/imessaging` image with `OC_IMESSAGING_RELAY_ENABLED=true`.
   - Exposes a narrowly scoped authenticated ingress endpoint (`POST /internal/v1/relay/push`), distinct from the private company worker API (`POST /internal/v1/execute`).
   - Enforces:
     - Out-of-band provisioned company gateway credentials (Bearer + HMAC-SHA256 signature over exact serialized bytes + timestamp + replay nonce; mTLS at ingress proxy).
     - Strict tenant/application/installation registration binding: the relay maps an **opaque `registrationHandle`** to its stored FCM token and **never** accepts an arbitrary raw FCM destination token from an untrusted tenant.
     - Rate limits, expiry checks, and the exact same opaque wake-up allowlist.
   - **Zero Secrets in APK**: The universal APK contains only public Firebase client configuration (or no Firebase config in push-off builds), never a service account key or universal relay secret.
   - **Zero Company Access**: The relay cannot connect to company gateways, cannot read conversations, and never receives company user access/refresh tokens.

### 2.3 Graceful Degradation and Invalid Token Handling

- When push is disabled or unconfigured (`OC_IMESSAGING_PUSH_MODE=disabled`, the default), `workers/imessaging` still reports base in-app capabilities (`messaging.inapp.send`, `messaging.inapp.receive`, `messaging.inapp.call`, `messaging.device.enrollment`) as ready while omitting `messaging.push.fcm`.
- Push provider outages, quota errors (`429`), or invalid/unregistered device tokens (`UNREGISTERED`) never block durable message persistence; the worker returns structured `providerStatus` (`sent`, `invalid_token`, `quota_exceeded`, `provider_unavailable`) so the gateway can clean up revoked registrations or apply bounded backoff.
- No OS background delivery guarantees are claimed; Android force-stop, Doze, DND, and OEM battery restrictions apply.

## 3. Consequences

- Universal APK deployments without a configured push relay operate with push visibly marked unavailable while direct realtime WSS delivery continues unaffected.
- Adding external messaging providers (WhatsApp, Telegram, Discord) remains out of scope and MUST NOT be advertised as ready capabilities.
