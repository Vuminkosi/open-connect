/**
 * Framework-neutral composer state machine.
 *
 * Mirrors the Blazor `ComposerSession` axes (spec §5): `displayState`,
 * `taskPhase`, `micState`, and `speechState`. Status text is derived from
 * real `SessionEvent` payloads only — this module never fabricates progress.
 *
 * Wire enum strings are kebab-case dotted event names (spec §9.1), e.g.
 * `task.queued`, `task.running`, `task.completed`, `task.failed`,
 * `task.cancelled`, `message.delta`, `message.completed`. Unknown event
 * types are ignored. No numeric enums are used anywhere in this package.
 *
 * This module is DOM-free and SSR-safe: importing it never touches
 * `document`, `window`, or `navigator`.
 *
 * The `ComposerSessionEvent` shape intentionally mirrors the
 * `SessionEvent` payload from `@openconnect/client` (`packages/client-ts`
 * `src/types.ts`, read-only here): same wire field names, same dotted
 * `eventType` vocabulary, same `sequence` ordering. A real
 * `OpenConnectClient` event object is structurally assignable wherever a
 * `ComposerSessionEvent` is expected, and this package never imports (or
 * connects) NATS.
 */

/** Wire schema version shared with the v1 contract. */
export type ComposerSchemaVersion = "openconnect.v1";

/**
 * Session event payload. Structural mirror of the `@openconnect/client`
 * `SessionEvent` (see module docblock); kept local so this package builds
 * without compiling `packages/client-ts` (which this task must not modify).
 */
export interface ComposerSessionEvent {
  schemaVersion: ComposerSchemaVersion;
  eventId: string;
  /** Dotted event name, e.g. "task.completed". */
  eventType: string;
  sessionId: string;
  sequence: number;
  occurredAt: string;
  correlationId?: string | null;
  payload: Record<string, unknown>;
}

/** Whether the anchored bubble is visible. */
export type DisplayState = "closed" | "open";

/**
 * Task lifecycle phase, derived from real `SessionEvent.eventType` values
 * (and the `TaskState` spellings of the v1 contract). `idle` means no task
 * has been accepted yet or the previous task reached a terminal phase and a
 * new draft cycle began.
 */
export type TaskPhase = "idle" | "queued" | "running" | "completed" | "failed" | "cancelled";

/** Microphone capture axis (spec §5). */
export type MicState = "off" | "listening" | "denied" | "unsupported";

/** Speech-synthesis playback axis (spec §5). */
export type SpeechState = "idle" | "speaking";

/** Terminal phases: no further events are expected for the task. */
const TERMINAL_PHASES: readonly TaskPhase[] = ["completed", "failed", "cancelled"];

export function isTerminalPhase(phase: TaskPhase): boolean {
  return TERMINAL_PHASES.includes(phase);
}

export type ComposerRole = "user" | "assistant";

export type ComposerMessageState = "received" | "streaming" | "failed" | "cancelled";

export interface ComposerMessage {
  role: ComposerRole;
  text: string;
  sequence: number;
  state: ComposerMessageState;
}

export interface ComposerState {
  displayState: DisplayState;
  taskPhase: TaskPhase;
  micState: MicState;
  speechState: SpeechState;
  /** Current editor draft (uncontrolled input mirrors into this on change). */
  draft: string;
  messages: readonly ComposerMessage[];
  /** Partial assistant text accumulated from `message.delta` events. */
  streamingText: string;
  /** Human-readable error, or `undefined` when there is none. */
  lastError: string | undefined;
  /** Highest `SessionEvent.sequence` applied so far. */
  lastSequence: number;
  /** Idempotency key of the in-flight submit, if any. */
  inFlightKey: string | undefined;
}

export function initialComposerState(): ComposerState {
  return {
    displayState: "closed",
    taskPhase: "idle",
    micState: "off",
    speechState: "idle",
    draft: "",
    messages: [],
    streamingText: "",
    lastSequence: 0,
    lastError: undefined,
    inFlightKey: undefined,
  };
}

export type ComposerAction =
  | { type: "open" }
  | { type: "close" }
  | { type: "toggle" }
  | { type: "draft-changed"; draft: string }
  | { type: "begin-send"; text: string; idempotencyKey: string }
  | { type: "apply-event"; event: ComposerSessionEvent }
  | { type: "mark-error"; message: string }
  | { type: "cancel-streaming" }
  | { type: "clear-error" }
  | { type: "mic-changed"; mic: MicState }
  | { type: "speech-changed"; speech: SpeechState };

function withBase(state: ComposerState, patch: Partial<ComposerState>): ComposerState {
  return { ...state, ...patch };
}

/**
 * Maps a real wire event type to a task phase. Returns `undefined` for
 * unknown types so callers can ignore them (never rendered, never faked).
 */
export function eventTypeToPhase(eventType: string): TaskPhase | undefined {
  const type = eventType.trim().toLowerCase();
  if (
    type === "task.queued" ||
    type === "task-queued" ||
    type.endsWith(".queued") ||
    type.endsWith("-queued")
  ) {
    return "queued";
  }
  if (
    type === "task.running" ||
    type === "task-running" ||
    type === "task.started" ||
    type.endsWith(".running") ||
    type.endsWith(".delta") ||
    type.endsWith("-delta")
  ) {
    return "running";
  }
  if (
    type === "task.completed" ||
    type === "task-completed" ||
    type === "message.completed" ||
    type === "message-completed" ||
    type.endsWith(".completed") ||
    type.endsWith(".done")
  ) {
    return "completed";
  }
  if (
    type === "task.failed" ||
    type === "task-failed" ||
    type.endsWith(".failed") ||
    type.endsWith(".error")
  ) {
    return "failed";
  }
  if (type === "task.cancelled" || type === "task-cancelled" || type.endsWith(".cancelled")) {
    return "cancelled";
  }
  if (type === "message" || type === "message.delta" || type === "message-delta") {
    return "running";
  }
  return undefined;
}

/** Extracts streamed text from a delta-style event payload, if present. */
function deltaText(event: ComposerSessionEvent): string {
  const payload = event.payload as Record<string, unknown>;
  const data = payload["data"];
  if (typeof data === "string") return data;
  const text = payload["text"];
  if (typeof text === "string") return text;
  const delta = payload["delta"];
  if (typeof delta === "string") return delta;
  return "";
}

/**
 * Pure reducer. `apply-event` advances `lastSequence` only for strictly
 * newer events and ignores unknown event types without re-render churn.
 */
export function composerReducer(state: ComposerState, action: ComposerAction): ComposerState {
  switch (action.type) {
    case "open":
      return withBase(state, { displayState: "open" });
    case "close":
      return withBase(state, { displayState: "closed" });
    case "toggle":
      return withBase(state, { displayState: state.displayState === "open" ? "closed" : "open" });
    case "draft-changed":
      return withBase(state, { draft: action.draft });
    case "begin-send": {
      const text = action.text.trim();
      if (text.length === 0 || state.taskPhase === "running" || state.taskPhase === "queued") {
        return state;
      }
      const message: ComposerMessage = {
        role: "user",
        text,
        sequence: state.lastSequence,
        state: "received",
      };
      return withBase(state, {
        messages: [...state.messages, message],
        streamingText: "",
        taskPhase: "queued",
        inFlightKey: action.idempotencyKey,
        lastError: undefined,
        draft: "",
      });
    }
    case "apply-event": {
      const event = action.event;
      if (event.sequence <= state.lastSequence && event.sequence !== 0) {
        return state;
      }
      const phase = eventTypeToPhase(event.eventType);
      if (phase === undefined) {
        return state;
      }
      const nextSequence = Math.max(state.lastSequence, event.sequence);
      if (phase === "running") {
        return withBase(state, {
          taskPhase: "running",
          streamingText: state.streamingText + deltaText(event),
          lastSequence: nextSequence,
        });
      }
      if (phase === "completed") {
        const text = (state.streamingText + deltaText(event)).trim();
        const done: ComposerMessage | null =
          text.length > 0
            ? { role: "assistant", text, sequence: event.sequence, state: "received" }
            : null;
        return withBase(state, {
          taskPhase: "completed",
          messages: done === null ? state.messages : [...state.messages, done],
          streamingText: "",
          inFlightKey: undefined,
          lastSequence: nextSequence,
        });
      }
      if (phase === "failed") {
        const failed: ComposerMessage = {
          role: "assistant",
          text: state.streamingText,
          sequence: event.sequence,
          state: "failed",
        };
        return withBase(state, {
          taskPhase: "failed",
          messages: [...state.messages, failed],
          streamingText: "",
          inFlightKey: undefined,
          lastError: "The assistant could not complete the request.",
          lastSequence: nextSequence,
        });
      }
      if (phase === "cancelled") {
        const cancelled: ComposerMessage | null =
          state.streamingText.length > 0
            ? {
                role: "assistant",
                text: state.streamingText,
                sequence: event.sequence,
                state: "cancelled",
              }
            : null;
        return withBase(state, {
          taskPhase: "cancelled",
          messages: cancelled === null ? state.messages : [...state.messages, cancelled],
          streamingText: "",
          inFlightKey: undefined,
          lastSequence: nextSequence,
        });
      }
      // "queued"
      return withBase(state, { taskPhase: "queued", lastSequence: nextSequence });
    }
    case "mark-error":
      return withBase(state, {
        taskPhase: "failed",
        lastError: action.message.trim().length > 0 ? action.message : "Something went wrong.",
        streamingText: "",
        inFlightKey: undefined,
      });
    case "cancel-streaming": {
      const cancelled: ComposerMessage | null =
        state.streamingText.length > 0
          ? {
              role: "assistant",
              text: state.streamingText,
              sequence: state.lastSequence,
              state: "cancelled",
            }
          : null;
      return withBase(state, {
        taskPhase: isTerminalPhase(state.taskPhase) ? state.taskPhase : "cancelled",
        messages: cancelled === null ? state.messages : [...state.messages, cancelled],
        streamingText: "",
        inFlightKey: undefined,
      });
    }
    case "clear-error":
      return withBase(state, { lastError: undefined });
    case "mic-changed":
      return withBase(state, { micState: action.mic });
    case "speech-changed":
      return withBase(state, { speechState: action.speech });
  }
}

/** Whether the send control may submit right now. */
export function canSend(state: ComposerState, sessionId: string | null | undefined): boolean {
  return (
    state.taskPhase !== "running" &&
    state.taskPhase !== "queued" &&
    state.draft.trim().length > 0 &&
    (sessionId ?? "").trim().length > 0
  );
}

/**
 * Status line derived from real task phase only (spec: no fake progress).
 * Mirrors the Blazor `StatusText` vocabulary.
 */
export function statusTextFor(state: ComposerState, sessionId: string | null | undefined): string {
  if ((sessionId ?? "").trim().length === 0) return "No session";
  switch (state.taskPhase) {
    case "running":
      return state.streamingText.length > 0 ? "Replying…" : "Working…";
    case "queued":
      return "Queued…";
    case "failed":
      return "Connection issue";
    case "cancelled":
      return "Cancelled";
    case "completed":
      return "Ready";
    case "idle":
      return "Ready";
  }
}

export interface SubmitKey {
  key: string;
  shiftKey: boolean;
  /** True while an IME composition session is active. */
  isComposing: boolean;
  /** True for auto-repeat keydown events (held key). */
  repeat: boolean;
  ctrlKey?: boolean | undefined;
  metaKey?: boolean | undefined;
}

/**
 * Enter submits; Shift+Enter inserts a newline; IME compositions and
 * held-Enter auto-repeats are ignored. Ctrl/Meta+Enter never submits here
 * (plain Enter is the submit key for this composer).
 */
export function shouldSubmitKey(event: SubmitKey): boolean {
  if (event.key !== "Enter") return false;
  if (event.shiftKey) return false;
  if (event.isComposing) return false;
  if (event.repeat) return false;
  if (event.ctrlKey === true || event.metaKey === true) return false;
  return true;
}

/** One idempotency key per submit (spec: submit carries a fresh key). */
export function createIdempotencyKey(): string {
  const cryptoRef =
    typeof globalThis.crypto !== "undefined" &&
    typeof globalThis.crypto.randomUUID === "function"
      ? globalThis.crypto
      : undefined;
  if (cryptoRef !== undefined) return cryptoRef.randomUUID();
  return `oc-${Date.now().toString(36)}-${Math.floor(Math.random() * 0xffffffff).toString(36)}`;
}
