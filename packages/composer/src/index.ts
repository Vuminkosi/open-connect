/**
 * `@openconnect/composer` — framework-neutral browser mini-composer.
 *
 * Importing this entry is DOM-free and SSR-safe: no `document`, `window`,
 * or `navigator` access happens at import time. Touch the DOM only via
 * `createMiniComposer(...).mount(...)`, and release everything with
 * `dispose()`.
 *
 * Pair with `styles.css` (import `"@openconnect/composer/styles.css"` in
 * bundlers) for the scoped `oc-` theme tokens.
 */
export {
  bubbleMetrics,
  clamp,
  createMiniComposer,
} from "./composer.js";
export type {
  BubbleMetrics,
  ComposerClient,
  ComposerListener,
  ComposerPlacement,
  MiniComposerHandle,
  MiniComposerOptions,
} from "./composer.js";
export {
  canSend,
  composerReducer,
  createIdempotencyKey,
  eventTypeToPhase,
  initialComposerState,
  isTerminalPhase,
  shouldSubmitKey,
  statusTextFor,
} from "./state.js";
export type {
  ComposerAction,
  ComposerMessage,
  ComposerMessageState,
  ComposerRole,
  ComposerSchemaVersion,
  ComposerSessionEvent,
  ComposerState,
  DisplayState,
  MicState,
  SpeechState,
  SubmitKey,
  TaskPhase,
} from "./state.js";
