# `@openconnect/navigator-react`

React provider + overlays for OpenConnect guided tours. This package
**composes** the existing OpenConnect provider and `MiniComposer` from
`@openconnect/composer/react` — it never replaces them and creates no
duplicate root provider: its context carries only the navigator handle.

- **SSR-safe.** Importing the package never touches `document`, `window`, or
  `matchMedia`. Subscriptions, focus management, highlight measurement, and
  disposal live inside effects guarded by `typeof` checks.
- **Scoped styling.** All styling is inline; no global CSS is shipped. The
  `oc-nav-*` class names are stable hooks if a host wants to layer its own
  theme on top.
- **Real tour state only.** The overlay renders the navigator's genuine
  snapshot (`status`, `index`, `steps`); no fake progress is ever shown.
- **Text-only announcements.** Screen-reader updates travel through an
  `aria-live="polite"` region. No speech-synthesis dependency: when speech is
  absent, the same text remains visible in the overlay card.

## Composition (required pattern)

```tsx
import { MiniComposer, OpenConnectProvider } from "@openconnect/composer/react";
import { OpenConnectNavigatorProvider } from "@openconnect/navigator-react";

// `navigator` is any structural OpenConnectNavigator handle
// (see "Bridge contract" below).
<OpenConnectProvider client={client} sessionId={sid} agentId={aid}>
  <OpenConnectNavigatorProvider navigator={navigator}>
    <MiniComposer assistantName="Assistant" suggestions={["Summarize"]} />
    <YourApp />
  </OpenConnectNavigatorProvider>
</OpenConnectProvider>;
```

Nesting order is flexible — the navigator provider also works outside the
OpenConnect provider — but always keep both providers: the composer owns
chat, the navigator owns tour overlays.

## Bridge contract

Until `@openconnect/navigator` (`NAV-CLI-01`) lands, this package defines a
minimal structural handle in `src/types.ts` so it builds standalone.
Any object with `getState` / `subscribe` / `next` / `back` / `skip` /
`pause` / `resume` / `dispose` satisfies it:

```ts
import type { OpenConnectNavigator } from "@openconnect/navigator-react";
```

TODO(NAV-CLI-01): reconcile with the `createOpenConnectNavigator` return
type once available; no call-site changes are expected.

## Behaviour

- Controls: **Back** (disabled on the first step), **Next**/**Finish**,
  **Pause**/**Resume**, **Skip tour**. All handlers are exception-safe.
- **Escape** pauses the tour without losing progress.
- Focus safety: focus moves into the card on each new step; the previously
  focused element is restored when the overlays unmount.
- Reduced motion (`prefers-reduced-motion`) disables highlight transitions;
  narrow viewports (`max-width: 599px`) render the card as a bottom sheet so
  host layouts never break.
- Missing or invalid `target` selectors never throw — the overlay card still
  carries the step copy.
- Unmount unsubscribes and, by default, calls `navigator.dispose()` (logout /
  teardown). Pass `disposeOnUnmount={false}` when the handle is owned
  elsewhere. `dispose()` must be idempotent.
- Omit `navigator` to render children only (pass-through while a tour is
  being set up).

## Verification

```bash
npm run typecheck   # tsc --noEmit
npm run build       # tsc -p tsconfig.build.json
npm test            # vitest run
```

NAV-AT-007: React SSR-safe; host MiniComposer + Navigator provider coexist.
