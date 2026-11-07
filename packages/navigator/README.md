# `@openconnect/navigator`

Framework-neutral, SSR-safe Navigator bridge for a read-only app tour:
explain, locate, highlight, scroll, focus, and internal navigation — with a
static text-only fallback that works on the server.

## Install

```bash
npm install @openconnect/navigator
```

For local development inside this repo the package resolves
`@openconnect/client` via `file:../client-ts`:

```bash
cd packages/navigator
npm install
```

## Quickstart

```ts
import { createOpenConnectNavigator } from "@openconnect/navigator";
// Optional overlay styling (style-isolated `ocnav-` classes):
import "@openconnect/navigator/styles.css";

const guide = createOpenConnectNavigator({
  // Same origin as your OpenConnectClient baseUrl
  // (which already ends in `/openconnect/v1`).
  baseUrl: "https://app.example/openconnect/v1",
  // Share the authenticated fetch wrapper you use for OpenConnectClient.
  // The bridge never stores credentials; auth travels per request.
  // getAuthorizationHeader: () => `Bearer ${token}`,
  routeId: "/editor",
  speech: { enabled: false }, // default: text-only fallback
  capture: { enabled: false }, // default: no content capture (NAV-AT-010)
});

// 1. Register targets on mount (re-registering bumps `revision` on remount).
guide.registry.register({
  targetId: "save",
  routeId: "/editor",
  label: "Save",
  description: "Save keeps your changes on this device.",
});

// 2. Bind only after auth + hydration (browser only touches the DOM here).
await guide.start();

// 3a. Static text tour — works on server and client, no DOM needed.
import { renderTextTour, validateTourSteps } from "@openconnect/navigator";
const steps = [{ title: "Save your work", body: "Press Save to keep your changes.", targetId: "save" }];
if (validateTourSteps(steps, guide.registry, "/editor").ok) {
  console.log(renderTextTour(steps));
}

// 3b. Overlay tour — falls back to text-only when no DOM is attached.
const tour = guide.runOverlayTour(steps, {
  onStep: (index) => console.log(`showing step ${index}`),
  onFinish: () => console.log("tour finished"),
});

// 4. Release everything (overlay, listeners, in-flight probes).
tour.stop();
guide.dispose();
```

### Commands and receipts

```ts
const receipt = await guide.dispatch({ intent: "explain", targetId: "save" });
// { receiptId, action, outcome: "completed" | "denied" | "redacted" | "failed" | "uncertain", detail, uncertainty? }
```

Read-only intents (`explain`, `locate`, `highlight`, `scroll`, `focus`)
are served locally against the target registry. `navigate` requires a
server grant (approved probe `GET /navigator/targets?routeId=`); without
one the receipt reports `denied` and the app stays on its current screen.

## Rules worth knowing

- **SSR-safe:** importing the package or creating the bridge never touches
  `window`/`document`. The DOM is bound only inside `start()` /
  `runOverlayTour()`.
- **Capture off by default:** snapshots are empty unless
  `capture: { enabled: true }` **and** the server grants capture. Form
  inputs are always redacted; hidden nodes, raw HTML, credential stores,
  browser storage, and cross-frame content are never collected.
- **Coexistence:** the bridge uses the client's public surface only and
  never alters `@openconnect/client` or MiniComposer registration.
- Overlay controls: Back / Next / Skip / Pause, `Escape` to close, focus
  is restored to the previously focused element, `prefers-reduced-motion`
  disables smooth scrolling, right-to-left layouts are honoured.
