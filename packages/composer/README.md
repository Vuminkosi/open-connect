# `@openconnect/composer`

Framework-neutral browser mini-composer for the OpenConnect v1 HTTP contract:
an anchored floating-action button (FAB, 2.75rem) plus bubble
(`min(23.5rem, viewport − 2rem)`) with header / editor / snippets / footer /
tail regions. Matches the Blazor `MiniComposer` regions and vocabulary.

- **SSR-safe.** Importing the package never touches `document`, `window`, or
  `navigator`. DOM is created only inside `mount()` (or React effects) and
  released by `dispose()` / effect cleanup.
- **Scoped styling.** All selectors use the `oc-` prefix with CSS variables;
  nothing leaks globally. Import `styles.css` in your bundler:
  `import "@openconnect/composer/styles.css"`.
- **Real status only.** The status line derives from genuine `SessionEvent`
  `eventType` values (`task.queued`, `task.running`, `message.delta`,
  `task.completed`, `task.failed`, `task.cancelled`, …). Unknown types are
  ignored; no fake progress is ever rendered.
- **Keyboard.** `Enter` submits, `Shift+Enter` inserts a newline, and IME
  compositions plus held-`Enter` auto-repeats are ignored. `Escape` closes.
- **Idempotent submits.** Every submit carries a fresh idempotency key sent
  as both `clientTaskId` and the `Idempotency-Key` header (via
  `@openconnect/client`).

## Vanilla usage

```ts
import { createMiniComposer } from "@openconnect/composer";
import "@openconnect/composer/styles.css";
import { OpenConnectClient } from "@openconnect/client";

const client = new OpenConnectClient({ baseUrl: "https://host/openconnect/v1" });
const composer = createMiniComposer({
  client,
  sessionId: "<session id>",
  agentId: "<agent id>",
  assistantName: "Assistant",
  suggestions: ["Summarize this page", "Draft a reply"],
});

// In the browser only:
composer.mount(document.body);
// …later:
composer.dispose();
```

## React usage (optional adapter)

```tsx
import { MiniComposer, OpenConnectProvider } from "@openconnect/composer/react";
import "@openconnect/composer/styles.css";

<OpenConnectProvider client={client} sessionId={sid} agentId={aid}>
  <MiniComposer assistantName="Assistant" suggestions={["Summarize"]} />
</OpenConnectProvider>;
```

`react` is an optional peer dependency; install it only when using the
adapter. The adapter creates no duplicate core client — it reuses the
`ComposerClient` you provide.

## API

- `createMiniComposer(options)` → `{ mount, dispose, send, open, close,
  toggle, attachSession, subscribe, getState }`
- `composerReducer` / `initialComposerState` — pure state machine
  (`displayState`, `taskPhase`, `micState`, `speechState` axes).
- `shouldSubmitKey`, `createIdempotencyKey`, `eventTypeToPhase`,
  `statusTextFor`, `canSend` — keyboard, idempotency, and status helpers.
- `clamp`, `bubbleMetrics` — bubble placement bounds (spec §6.1).

## Verification

```bash
npm run typecheck   # tsc --noEmit
npm run build       # tsc -p tsconfig.build.json
npm test            # vitest run
node -e "import('./dist/index.js').then(m => console.log(Object.keys(m)))"
```
