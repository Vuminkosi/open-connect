# OpenConnect Anchor Composer — Product & Acceptance Requirements


**Document:** `composer-requirements.md`  
**Version:** 1.0-draft · **Date:** 2026-11-07  
**Status:** Normative implementation specification; no feature assumed implemented  
**Companion:** [`composer-spec.md`](./composer-spec.md)  
**Target packages:** `OpenConnect.Components.Blazor`, `OpenConnect.Client`, `@openconnect/client`, and optional `@openconnect/react`/browser components.

## 0. Objective and immutable design decision

**OpenConnect's default end-user interaction surface SHALL replicate the compact anchored composer from the supplied reference image and reviewed source, not create a full-screen chat application, sidebar, large modal, or permanent chat history panel.** The SDK must disappear into a developer's application until the user explicitly calls upon it, preserving application workspace. A floating bottom-right launcher opens a small conversation bubble immediately above it; the user can drag the bubble to another safe location. Text tasks, status, voice input/output, session stop, settings and context-aware UI guidance all operate through the same minimal surface.

The anchor composer is the **shared user-facing entry** into the default OpenConnect lead agent, permitted subagents, tools, MCP, `IOpenConnectComponent` discovery, Navigator, and optional speech workers. It is *not* an independent AI agent, model runtime, transport, security policy, or replacement for host UI. All actual task routing, durable events, privacy and authorization are owned by the OpenConnect host.

**MUST/MUST NOT** are release-blocking. **SHOULD** needs documented deviation; **MAY** optional. IDs `ACR-*` map to `composer-spec.md` implementation work items `CMP-IMP-*` and tests `CMP-AT-*`. No acceptance is complete without reproducible evidence.

## 1. Reviewed reference versus design interpretation

Repository reviewed: [`Vuminkosi/Swift-Aid`](https://github.com/Vuminkosi/Swift-Aid), pinned tree SHA `1c98e0a1efc94ac517805a6454216fde7b6e8743`. Relevant observed source:

- `src/SwiftAid.UI/Ellai/Components/EllaiCompactComposer.razor` and `.razor.css`: compact input, header, status, audio controls, drag handle, callbacks, sizing and message snippets.
- `src/SwiftAid.UI/Ellai/Components/EllaiLauncherMenu.razor` and `.razor.css`: independent, fixed `MudFabMenu` launcher and action items.
- `src/SwiftAid.UI/Ellai/Guidance/EllaiGuidanceHost.razor` and `.razor.css`: floating versus attached presentation, session state and latest messages, tour attachment and position binding.
- `src/SwiftAid.UI/Ellai/Components/EllaiStatusLine.razor` and `.razor.css`: three-bar animated status, safe structured text, expanded status, reduced motion.
- `src/SwiftAid.UI/Ellai/EllaiComposerPosition.cs`, `wwwroot/js/ellai-composer-drag.js`, `wwwroot/js/ellai-composer-input.js`, corresponding `tests/voice/*.test.mjs`: clamp, move by drag/keyboard, callback/disposal, Enter/Shift+Enter and IME safeguards.

The supplied reference screenshot is **634×326 pixels** and shows an *open bubble*: thin-bordered white rounded rectangle, small move handle at upper left, settings/stop/close at upper right, "Message Ellai" and "Ask Ellai..." input, red stop and grey live audio controls, a dotted separator, `Ready` with green animated bars, `Voice on`, `Mic on`, and a distinct green circular assistant FAB below/right. It is a cropped snapshot—not a reliable measurement of underlying CSS viewport scaling, not proof that every state is functional.

Source specifics to preserve: source composer declares `inline-size: min(24rem, calc(100vw - 2rem))`; source host floating width `min(23.5rem, calc(100vw - 2rem))`, with max-height bounded by viewport; launch button 2.75rem square and positioned `20vh` from bottom and at least 1rem from right; drag handle has keyboard arrow movement of 24 CSS pixels and Home reset; input MaxLength 8000, one initial row, grows to five lines; source status response area max 12rem height, latest user text max 4.5rem, partial transcript max 4.5rem. The OpenConnect default MAY intentionally change FAB vertical placement to the user's previously specified *bottom right*, but MUST retain the anchored bubble visual language, tiny footprint, and a configurable legacy `20vh` placement preset. This difference is an explicit design change, not source behavior.

## 2. Non-negotiable user experience

- **ACR-UX-001:** Closed/minimized state takes no more than one small FAB button footprint (target 44–56 CSS px touch area), no full-height overlay or reserved layout column. No automatic pop-up on first load by default.
- **ACR-UX-002:** Expanded state is a **popover-like compact bubble**, anchored above and aligned to the FAB with a small notch/tail. It floats over the host document and does not cause surrounding layout reflow, horizontal scroll, or resize.
- **ACR-UX-003:** Default anchor is bottom-right with safe-area padding; offer typed enum positions (`BottomRight`, `BottomLeft`, `TopRight`, `TopLeft`, `Custom`) and optional screenshot-faithful `LegacyViewport20Vh` preset. On narrow viewports, clamp width to viewport minus margins and keep input/buttons visible.
- **ACR-UX-004:** The bubble must visually resemble the reference image: pale/host-themed surface, subtle stroke/shadow, rounded corners, small drag glyph at left, small settings/stop/close at right, a labeled text field, context-sensitive right-end icons, subdued divider, tiny status indicator at bottom-left and compact voice/mic controls at bottom-right.
- **ACR-UX-005:** Drag is **handle-only**; input, buttons and other page elements remain interactive. Pointer/touch drag stays inside visual viewport and updates an optional persisted position; keyboard focus on handle accepts arrows (24 CSS px increments by default) and Home to reset. Position must re-clamp on resize, zoom, orientation and virtual keyboard changes. Drag disabled in attached/in-flow mode.
- **ACR-UX-006:** Opening always gives predictable focus to the input when not loading/listening; Escape returns to launcher if not dismissing a nested dialog; closing restores launcher focus and does not silently cancel a durable task unless close policy is explicitly `Cancel`.
- **ACR-UX-007:** User may close/minimize composer, navigate the host app or reconnect without losing the authorized durable task/session. The launcher can show a restrained unread/active indicator without intrusive badges or constant motion.
- **ACR-UX-008:** No forced OpenConnect brand name: host app supplies label, placeholder, launcher icon, accent tokens, header/help text and optional empty-state suggestions. Safe neutral default name is "Assistant"; do not retain hardcoded "Ellai" in public SDK.
- **ACR-UX-009:** Host CSS, MudBlazor themes and JavaScript apps must not be overridden by broad selectors; style via local CSS isolation (Blazor) or scoped CSS variables/Shadow DOM strategy (web) with an explicit documented theme bridge.
- **ACR-UX-010:** Attached mode (e.g. Navigator tour card) reuses the *same composer content* without FAB and without floating positioning; not a second behavior implementation.

## 3. Launcher actions and visual anatomy

- **ACR-VIS-001:** Header row uses `Move` on left, `Settings`, `Stop session` and `Close` on right. Stop is disabled or hidden when no active/cancellable session; close remains operable while disconnected.
- **ACR-VIS-002:** Text input has a small label above/within field (`Message {AssistantName}`), placeholder (`Ask {AssistantName}…`), immediate editing state, auto-grow 1–5 lines, 8000-character configurable limit (hard server limit), and right-hand action cluster that never overlays typed text.
- **ACR-VIS-003:** Empty input shows a live-voice action (if available) and recorded mic action where relevant; nonempty valid input replaces the live-voice action with Send. When capturing, recorded mic becomes Stop; live voice shows an active waveform/graphic equalizer control and Stop. Disabled actions must expose truthful description, never pretend mic support.
- **ACR-VIS-004:** Response text and last sent message are concise **inline snippets** between field and status; they scroll within bounded local regions rather than turning the composer into an unbounded transcript. Richer history is an *optional explicit action* (view conversation in another host-approved surface).
- **ACR-VIS-005:** Bottom status row shows animated three-bar indicator and plain-language status (`Ready`, `Connecting…`, `Thinking…`, `Listening`, `Speaking`, `Waiting for approval`, `Reconnecting…`, `Error`); status long text is clamped/expandable, media links sanitized. Animation respects reduced motion.
- **ACR-VIS-006:** Optional audio footer shows small `Voice on`/`Listen` toggle and, when voice playback enabled, mic on/off state, with visible active/disabled semantic indicators. When no speech capability exists, omit nonfunctional toggles; text-first composer stays fully usable.
- **ACR-VIS-007:** Settings opens a small, explicit view for voice preferences, privacy notice, worker status, accessibility and available actions; no full settings page required. Dangerous settings never changed directly by model-generated UI.
- **ACR-VIS-008:** Launcher is a distinct circular FAB with primary accent and assistant/robot icon by default. Optional menu modes are derived from *authorized capability maps* (Ask, Show Me, Guide Me, Help Me Work); no fixed healthcare/business-specific choices or unavailable mode entries.
- **ACR-VIS-009:** For minimal mode, a single FAB click opens Ask composer; alternate modes may appear in optional FAB menu when multiple enabled capabilities exist.
- **ACR-VIS-010:** Visual fidelity must be verified with golden screenshots at reference-like desktop crop, phone viewport, dark theme, 200% zoom and small landscape. Layout targets in `composer-spec.md` are intended CSS targets, not claims extracted as pixel-perfect from the screenshot.

## 4. Text interaction, task and event lifecycle

- **ACR-TXT-001:** Enter submits valid nonempty text, Shift+Enter inserts newline, IME composition does not submit, held Enter cannot send twice, and disabled/cancelled states reject submission. Use the most recent DOM value for user gesture to avoid stale asynchronous binding races; server remains authoritative.
- **ACR-TXT-002:** Submit creates a task with a client idempotency key, maps to an authorized session and lead agent, and records accepted/processing/final state through existing OpenConnect API. A duplicated click/keydown or reconnect must not duplicate committed turns.
- **ACR-TXT-003:** Cancel (`Stop`) is distinct from Close/Minimize: it cancels active task and/or session according to explicit typed policy. The UI renders `Cancelling…`, waits for authoritative receipt, handles unknown outcome and retains a meaningful recovery action.
- **ACR-TXT-004:** UI status is derived from *real* gateway task/agent/tool/voice events over existing HTTP(S)+WS(S), never timers pretending execution or synthetic subagent progress. NATS JetStream remains internal to gateway/outbox; browser never connects to NATS.
- **ACR-TXT-005:** Accept partial streamed text and final sanitized response; maintain compact latest-user/latest-answer views. Reconnect uses session cursor, deduplication and user-scope authorization; uncommitted deltas may be reset from a durable snapshot.
- **ACR-TXT-006:** Model/tool outputs require default-on firewall and output inspection before being displayed/restored; tool authorization/capability discovery controlled on server. No model-generated arbitrary Razor, HTML, JS or client action.
- **ACR-TXT-007:** Detect and describe disconnected, offline, authorization expired, policy blocked, approval pending, invalid input, capability unavailable and server error states without trapping user input.
- **ACR-TXT-008:** Interactive agents use current authenticated user's execution context; operational runs use workload identity and are not automatically exposed in an unrelated user's composer. All component suggestions are filtered by the active user's grants.
- **ACR-TXT-009:** `IOpenConnectComponent` proposals can be activated in an optional scoped supplementary host-approved renderer; compact bubble displays a minimal link/button/summary and remains small.
- **ACR-TXT-010:** No prompt text, speech bytes, sensitive DOM data, bearer tokens or placeholder-to-PII mappings logged in frontend analytics or NATS events.

## 5. Speech and microphone behavior

- **ACR-AUD-001:** Voice output, recorded speech transcription, live speech dialog and microphone capture are **independent capabilities**, advertised by actual healthy workers/browser permissions; none are prerequisites for text composition or MCP.
- **ACR-AUD-002:** The voice toggle never implicitly asks for microphone access; mic permission is requested only from an explicit user gesture, with clear stop and indicator. Closing/leaving session stops active capture as policy requires.
- **ACR-AUD-003:** Recorded mic: start -> capture -> stop -> transcribe -> optionally auto-submit only once if configured and transcript accepted. Live mic: start -> VAD/turn recognition -> automatic accepted turn(s) -> stay listening until stop. Both use distinct user-facing state.
- **ACR-AUD-004:** Voice output uses OpenConnect's optional Kokoro/MMS-TTS routing and real speech capability metadata; default Kokoro English `af_heart` only when installed and healthy. If no TTS exists, voice UI hides/degrades gracefully and text remains complete.
- **ACR-AUD-005:** Output speech playback and input mic state must be independently toggled; `Voice on` does not imply `Mic on`, nor vice versa. Stop speech on barge-in; fence late audio chunks by speech epoch.
- **ACR-AUD-006:** Speech streams require appropriate codec/sample-rate/channel metadata, permissions, interruption and cancellation. No unauthorized external STT for strict-local audio; unsupported privacy coverage is blocked.
- **ACR-AUD-007:** UI shall disclose unavailable permission/insecure context/device, inactive worker, resource overload or explicit language mismatch. Never render fake waveform indicating recording when no capture is active.
- **ACR-AUD-008:** Browser autoplay must be activated through a user gesture; no surprise audio. Screen readers get status text and transcript even without speech.
- **ACR-AUD-009:** Stopping a live microphone session does not automatically disable response speech playback. Voice output may continue if the user leaves it on.
- **ACR-AUD-010:** Audio and transcription are optional across .NET Blazor, TS browser, and Node headless; Node headless SDK supports task APIs but does not expose browser microphone/UI functionality.

## 6. SDK integration and feature ownership

- **ACR-SDK-001:** Blazor integration: `AddOpenConnectClient(configure)` for task API and WS address; `<OpenConnectProvider>` in root/layout; `<MiniComposer />` anywhere inside provider scope (or layout-level default anchor). Do not register duplicate MudBlazor popover/dialog providers if already present.
- **ACR-SDK-002:** TypeScript package `@openconnect/client` remains headless/browser-neutral and supports existing fetch/WS client, versioned contracts and abort/reconnect. A framework-neutral browser UI package `@openconnect/composer` renders the same design; optional React adapter `@openconnect/react` (or a separate subpath) composes with existing client. A Node process MUST NOT require DOM globals.
- **ACR-SDK-003:** `MiniComposer` is a single **public design/API name** across platforms (Blazor component and browser/React renderable) with shared type/state/event behavior and design tokens. Pixel-perfect replication must be validated via cross-platform screenshots, not duplicated by hand-wavy styling.
- **ACR-SDK-004:** Both SDKs use same canonical OpenAPI/JSON schema events, common agent session IDs, client task idempotency and runtime status projection. No second backend channel, agent coordinator or token vault.
- **ACR-SDK-005:** Developers configure strongly typed options/enums for anchor position, close behavior, initial mode, voice visibility, input policy, status presentation, appearance and optional capability menus. Custom labels and brand tokens are validated strings.
- **ACR-SDK-006:** Composer actions use existing client task API, voice/router SDK and Navigator capability hooks. Disabling Navigator, voice, Playwright or other worker never disables the text composer.
- **ACR-SDK-007:** The UI must not self-grant cross-origin tool access, run arbitrary commands or mutate business data; all suggestions, Navigator commands and component renderers undergo host authorization.
- **ACR-SDK-008:** Native browser composer must be safe with SPA routing, SSR, hydration, Shadow DOM optionality and host app CSS. Multiple render roots are supported without event-handler duplication or leaked global CSS.
- **ACR-SDK-009:** Blazor Server and WebAssembly require tested rendering lifecycle; auto render mode must not execute JS interop before hydration or retain stale DotNetObjectReference after disconnect.
- **ACR-SDK-010:** No `Ellai`, SwiftAid, healthcare or application-domain literal labels in packaged SDK runtime; retain correct license/copyright attribution where actually required by copied code in a separate legal ledger.

## 7. Accessibility, responsiveness, compatibility

- **ACR-A11Y-001:** WCAG 2.2 AA targets, visible focus, semantic buttons/inputs and status `role=status`, labels for all icons; no pointer-only drag, keyboard trap or screen-reader inaccessible active/disabled control.
- **ACR-A11Y-002:** Accessible voice states announce `Microphone on/off`, `Voice playback on/off`, not just icon color. Recording permissions and stop are available by keyboard and touch.
- **ACR-A11Y-003:** When browser reports `prefers-reduced-motion`, suppress bar-wave and glowing animations while preserving state labels.
- **ACR-A11Y-004:** Maintain usable 44×44 CSS pixel target hitboxes on touch screens where possible; visual icon may remain smaller than hitbox. No minimum target sizes that make the bubble overflow narrow screens.
- **ACR-A11Y-005:** RTL, browser zoom, device safe-area insets, soft keyboard and mobile visualViewport must not strand the composer offscreen.
- **ACR-A11Y-006:** Honor high contrast and host font scaling; enforce minimum legible status typography and avoid relying on red/green alone for error/recording state.
- **ACR-A11Y-007:** No automatic focus/voice permission prompts on page load; user invocation is the activation boundary.

## 8. Lifecycle and persistence

- **ACR-LIFE-001:** Persist only safe UI preferences per authorized user/app: launcher corner, normalized drag offset, voice-enabled preference when consented, last open/closed state if explicitly configured, and dismissed introduction; never store raw messages or tokens in localStorage.
- **ACR-LIFE-002:** Close minimizes UI and disposes browser media/capture correctly; task may continue on server. Stop actively cancels selected task/session per configured policy. Logout clears scoped local state/permissions, disposes event listeners and avoids cross-user reuse.
- **ACR-LIFE-003:** Drag/input bridge attach must be idempotent; browser detachment/Blazor unmount while async module loading must not resurrect listeners. No leaked `ResizeObserver`, `MutationObserver`, audio context, WS subscription or timer.
- **ACR-LIFE-004:** Double mount/React StrictMode and Blazor rerender must not initialize duplicate composers or subscribe twice.
- **ACR-LIFE-005:** Composer may be placed in attached layout during Navigator/onboarding and return to floating anchor with coherent state, no duplicate sessions or keyboard listeners.

## 9. Operational and CI requirements

- **ACR-OPS-001:** Bundle size measured and published for the web UI; no dependency on Python models, Chromium or TTS just to render composer. Lazy-load interaction JS and any optional audio modules.
- **ACR-OPS-002:** `OpenConnect.Components.Blazor` uses MudBlazor components where appropriate; browser UI uses standards-based DOM/CSS with equivalent interaction semantics, not a NuGet-to-JS dependency.
- **ACR-OPS-003:** Tests include bUnit, JS unit tests, Playwright screenshot regression, accessibility scan, package-consumer installs and auth/WS integration against a real gateway.
- **ACR-OPS-004:** CI must enforce visual screenshot baselines from supplied reference-style design across mobile/tablet/desktop, light/dark, no voice/voice active/status long/keyboard/drag.
- **ACR-OPS-005:** NuGet/npm release workflows publish composer packages with source links/symbols/types and dependency compatibility. Release only after clean consuming Blazor and TypeScript/React apps can mount, open, send, reconnect, close and dispose without layout regression.
- **ACR-OPS-006:** No model/worker download when launching text-only composer; capability absence is not shown as a fatal global error.

## 10. Acceptance test matrix

| ID | Scenario | Passing condition |
|---|---|---|
| CMP-AT-001 | Closed minimal UI | Only FAB visible, no reserved layout width or unsummoned overlay |
| CMP-AT-002 | Open anchor geometry | Compact bubble and tail align with FAB; no viewport overflow |
| CMP-AT-003 | Reference screenshot parity | Header/input/divider/status/voice/mic controls visually faithful at target crop |
| CMP-AT-004 | Close versus Stop | Close hides but does not silently cancel; Stop dispatches scoped cancellation |
| CMP-AT-005 | Drag pointer + keyboard | Handle only, 24px arrows, Home reset, resize clamp, no offscreen state |
| CMP-AT-006 | Enter + IME | Single current-value submit; Shift+Enter newline; IME/held Enter ignored |
| CMP-AT-007 | Text-only environment | No TTS/STT/browser worker; send/response/status still work |
| CMP-AT-008 | Control conditionality | Empty field live icon, filled field Send, active recording Stop and waveform |
| CMP-AT-009 | Output status/progress | Real `Ready/Thinking/Reconnecting/...` from authorized events, no fake task lifecycle |
| CMP-AT-010 | Mic/voice independent state | Voice/mic toggled separately; stopping mic leaves playback setting intact |
| CMP-AT-011 | Real voice worker | Enabled installed capability, correct audio metadata, speech epoch and stop |
| CMP-AT-012 | Offline/unavailable microphone | No unauthorized permission prompt; readable disabled/unavailable state |
| CMP-AT-013 | Compact response | Latest messages scroll in bounded regions; bubble does not become large transcript |
| CMP-AT-014 | Reconnect and idempotency | No duplicated accepted turn, durable session restored within scope |
| CMP-AT-015 | Auth change | Logout/user switch clears scoped bridge/capability/session UI; no leak |
| CMP-AT-016 | Blazor external consumer | Layout Provider + MiniComposer mounts in tested render modes; no duplicate Mud providers |
| CMP-AT-017 | TS/React external consumer | Browser UI mounts; Node SSR import without DOM access succeeds |
| CMP-AT-018 | A11y | Labels, keyboard focus, touch hitboxes, reduced motion, contrast tests pass |
| CMP-AT-019 | Mobile/RTL/zoom | Within viewport and safe areas, keyboard does not cover composer |
| CMP-AT-020 | Component/Navigator integration | Authorized suggestion triggers standard task; attached tour uses same content |
| CMP-AT-021 | Privacy/observability | No raw canary prompt/PII/audio or user credentials in unsafe sinks |
| CMP-AT-022 | Event state and replay | Accepted/delta/final/stop status reconciles across WS reconnect |
| CMP-AT-023 | Package build and release | NuGet/npm OIDC workflow and clean-consumer smoke pass |
| CMP-AT-024 | Cleanup/StrictMode | Unmount/re-attach doesn't leak duplicate handlers/audio/WS observers |

## 11. Release phases and definition of done

**P0:** reproduce shell, FAB, input, drag, status, close/stop and text-only gateway interaction in Blazor and TypeScript/browser. **P1:** voice capture/playback and conditional actions with honest capability detection. **P2:** authorized component suggestions/Navigator attachment, settings/menu, enhanced accessibility, dark/mobile/RTL hardening. **P3:** package publishing/visual regression in CI and docs examples; release of P0 only as clearly labeled limited preview if voice and Navigator remain absent. Each release must preserve all core security tests, and no voice feature may be advertised without real worker evidence.

## 12. Reference and migration hygiene

Sources were inspected on public GitHub at pinned tree SHA `1c98e0a1efc94ac517805a6454216fde7b6e8743`, and the screenshot was supplied by the user. This is evidence of source structure, **not** proof a clean OpenConnect runtime already exists or its tests pass. For code extraction, inspect repository license, copyright headers and third-party licenses; record source SHA and treatment in migration ledger. Do not copy domain-specific application state, credentials, patient data or healthcare-only modes into OpenConnect. Product-facing package documentation should use generic OpenConnect/host terminology; legal notices must remain as required.
