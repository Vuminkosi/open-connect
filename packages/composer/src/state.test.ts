import { describe, expect, it } from "vitest";
import { bubbleMetrics, clamp, createMiniComposer } from "./composer.js";
import {
  canSend,
  composerReducer,
  createIdempotencyKey,
  eventTypeToPhase,
  initialComposerState,
  isTerminalPhase,
  shouldSubmitKey,
  statusTextFor,
  type ComposerSessionEvent,
} from "./state.js";

function sessionEvent(overrides: Partial<ComposerSessionEvent> = {}): ComposerSessionEvent {
  return {
    schemaVersion: "openconnect.v1",
    eventId: "evt-1",
    eventType: "task.running",
    sessionId: "session-1",
    sequence: 1,
    occurredAt: "2026-01-01T00:00:00.000Z",
    payload: {},
    ...overrides,
  };
}

describe("module import is DOM-free (SSR-safe)", () => {
  it("does not require a DOM at import time", () => {
    expect(typeof document).toBe("undefined");
  });

  it("creating a composer does not touch the DOM", () => {
    expect(() => createMiniComposer({ sessionId: "session-1" })).not.toThrow();
    expect(typeof document).toBe("undefined");
  });

  it("mount without a DOM throws a clear error instead of crashing", () => {
    const composer = createMiniComposer({ sessionId: "session-1" });
    expect(() => composer.mount()).toThrowError(/requires a DOM/);
  });
});

describe("shouldSubmitKey", () => {
  it("submits on plain Enter", () => {
    expect(
      shouldSubmitKey({ key: "Enter", shiftKey: false, isComposing: false, repeat: false }),
    ).toBe(true);
  });

  it("ignores Shift+Enter (newline)", () => {
    expect(
      shouldSubmitKey({ key: "Enter", shiftKey: true, isComposing: false, repeat: false }),
    ).toBe(false);
  });

  it("ignores IME composition Enter", () => {
    expect(
      shouldSubmitKey({ key: "Enter", shiftKey: false, isComposing: true, repeat: false }),
    ).toBe(false);
  });

  it("ignores held-Enter auto-repeat", () => {
    expect(
      shouldSubmitKey({ key: "Enter", shiftKey: false, isComposing: false, repeat: true }),
    ).toBe(false);
  });

  it("ignores Ctrl/Meta+Enter and other keys", () => {
    expect(
      shouldSubmitKey({
        key: "Enter",
        shiftKey: false,
        isComposing: false,
        repeat: false,
        ctrlKey: true,
      }),
    ).toBe(false);
    expect(
      shouldSubmitKey({
        key: "Enter",
        shiftKey: false,
        isComposing: false,
        repeat: false,
        metaKey: true,
      }),
    ).toBe(false);
    expect(
      shouldSubmitKey({ key: "a", shiftKey: false, isComposing: false, repeat: false }),
    ).toBe(false);
  });
});

describe("createIdempotencyKey", () => {
  it("issues a fresh non-empty key per submit", () => {
    const first = createIdempotencyKey();
    const second = createIdempotencyKey();
    expect(first.length).toBeGreaterThan(0);
    expect(second.length).toBeGreaterThan(0);
    expect(first).not.toBe(second);
  });
});

describe("eventTypeToPhase", () => {
  it.each([
    ["task.queued", "queued"],
    ["task.running", "running"],
    ["message.delta", "running"],
    ["message", "running"],
    ["task.completed", "completed"],
    ["message.completed", "completed"],
    ["task.failed", "failed"],
    ["task.cancelled", "cancelled"],
    ["TASK.FAILED", "failed"],
  ] as const)("maps %s to %s", (eventType, phase) => {
    expect(eventTypeToPhase(eventType)).toBe(phase);
  });

  it("returns undefined for unknown types (never fakes progress)", () => {
    expect(eventTypeToPhase("widget.sprocket")).toBeUndefined();
    expect(eventTypeToPhase("")).toBeUndefined();
  });
});

describe("isTerminalPhase", () => {
  it("treats completed/failed/cancelled as terminal", () => {
    expect(isTerminalPhase("completed")).toBe(true);
    expect(isTerminalPhase("failed")).toBe(true);
    expect(isTerminalPhase("cancelled")).toBe(true);
    expect(isTerminalPhase("running")).toBe(false);
    expect(isTerminalPhase("idle")).toBe(false);
  });
});

describe("composerReducer", () => {
  it("begins a send with a user message and the submit key", () => {
    const next = composerReducer(initialComposerState(), {
      type: "begin-send",
      text: "  hello  ",
      idempotencyKey: "key-1",
    });
    expect(next.taskPhase).toBe("queued");
    expect(next.messages).toHaveLength(1);
    expect(next.messages[0]).toMatchObject({ role: "user", text: "hello" });
    expect(next.inFlightKey).toBe("key-1");
    expect(next.draft).toBe("");
  });

  it("ignores empty sends and sends while a task is in flight", () => {
    const start = initialComposerState();
    const empty = composerReducer(start, {
      type: "begin-send",
      text: "   ",
      idempotencyKey: "key-1",
    });
    expect(empty).toBe(start);
    const busy = composerReducer(
      { ...initialComposerState(), taskPhase: "running" },
      { type: "begin-send", text: "hello", idempotencyKey: "key-2" },
    );
    expect(busy.messages).toHaveLength(0);
  });

  it("accumulates delta text while running, then finalizes on completed", () => {
    let state = composerReducer(initialComposerState(), {
      type: "begin-send",
      text: "hi",
      idempotencyKey: "key-1",
    });
    state = composerReducer(state, {
      type: "apply-event",
      event: sessionEvent({ eventType: "task.running", sequence: 1, payload: { data: "Hel" } }),
    });
    state = composerReducer(state, {
      type: "apply-event",
      event: sessionEvent({ eventType: "message.delta", sequence: 2, payload: { data: "lo" } }),
    });
    expect(state.taskPhase).toBe("running");
    expect(state.streamingText).toBe("Hello");
    state = composerReducer(state, {
      type: "apply-event",
      event: sessionEvent({ eventType: "task.completed", sequence: 3, payload: {} }),
    });
    expect(state.taskPhase).toBe("completed");
    expect(state.streamingText).toBe("");
    expect(state.inFlightKey).toBeUndefined();
    expect(state.messages[state.messages.length - 1]).toMatchObject({
      role: "assistant",
      text: "Hello",
    });
  });

  it("records failure from real failed events", () => {
    let state = composerReducer(initialComposerState(), {
      type: "begin-send",
      text: "hi",
      idempotencyKey: "key-1",
    });
    state = composerReducer(state, {
      type: "apply-event",
      event: sessionEvent({ eventType: "task.failed", sequence: 1, payload: {} }),
    });
    expect(state.taskPhase).toBe("failed");
    expect(state.lastError).toBe("The assistant could not complete the request.");
  });

  it("ignores stale sequences and unknown event types", () => {
    const started = composerReducer(initialComposerState(), {
      type: "begin-send",
      text: "hi",
      idempotencyKey: "key-1",
    });
    const advanced = composerReducer(started, {
      type: "apply-event",
      event: sessionEvent({ eventType: "task.running", sequence: 5, payload: {} }),
    });
    const stale = composerReducer(advanced, {
      type: "apply-event",
      event: sessionEvent({ eventType: "task.completed", sequence: 2, payload: {} }),
    });
    expect(stale).toBe(advanced);
    const unknown = composerReducer(advanced, {
      type: "apply-event",
      event: sessionEvent({ eventType: "mystery.happened", sequence: 6, payload: {} }),
    });
    expect(unknown).toBe(advanced);
  });

  it("toggles display state and tracks mic/speech axes", () => {
    let state = initialComposerState();
    state = composerReducer(state, { type: "toggle" });
    expect(state.displayState).toBe("open");
    state = composerReducer(state, { type: "mic-changed", mic: "listening" });
    state = composerReducer(state, { type: "speech-changed", speech: "speaking" });
    expect(state.micState).toBe("listening");
    expect(state.speechState).toBe("speaking");
    state = composerReducer(state, { type: "close" });
    expect(state.displayState).toBe("closed");
  });
});

describe("canSend / statusTextFor", () => {
  it("requires a draft, a session, and no in-flight task", () => {
    const withDraft = { ...initialComposerState(), draft: "hello" };
    expect(canSend(withDraft, "session-1")).toBe(true);
    expect(canSend(withDraft, null)).toBe(false);
    expect(canSend(withDraft, "  ")).toBe(false);
    expect(canSend({ ...withDraft, taskPhase: "running" }, "session-1")).toBe(false);
    expect(canSend(initialComposerState(), "session-1")).toBe(false);
  });

  it("derives status from real phase only", () => {
    expect(statusTextFor(initialComposerState(), null)).toBe("No session");
    expect(statusTextFor(initialComposerState(), "s")).toBe("Ready");
    expect(statusTextFor({ ...initialComposerState(), taskPhase: "queued" }, "s")).toBe("Queued…");
    expect(statusTextFor({ ...initialComposerState(), taskPhase: "running" }, "s")).toBe(
      "Working…",
    );
    expect(
      statusTextFor({ ...initialComposerState(), taskPhase: "running", streamingText: "hi" }, "s"),
    ).toBe("Replying…");
    expect(statusTextFor({ ...initialComposerState(), taskPhase: "failed" }, "s")).toBe(
      "Connection issue",
    );
  });
});

describe("clamp / bubbleMetrics", () => {
  it("clamps placement values", () => {
    expect(clamp(5, 0, 10)).toBe(5);
    expect(clamp(-1, 0, 10)).toBe(0);
    expect(clamp(99, 0, 10)).toBe(10);
    expect(clamp(Number.NaN, 2, 8)).toBe(2);
  });

  it("sizes the bubble as min(23.5rem, viewport − 2rem)", () => {
    expect(bubbleMetrics(800, 600)).toEqual({ widthPx: 376, maxHeightPx: 480 });
    expect(bubbleMetrics(300, 500)).toEqual({ widthPx: 268, maxHeightPx: 380 });
    expect(bubbleMetrics(2000, 2000)).toEqual({ widthPx: 376, maxHeightPx: 560 });
  });
});
