/**
 * Framework-neutral mini-composer DOM factory.
 *
 * `createMiniComposer()` is SSR-safe: importing this module never touches
 * `document`, `window`, or `navigator`. All DOM work happens inside `mount()`,
 * and `dispose()` removes every listener, observer, subscription, and node
 * the mount created.
 *
 * The rendered structure matches the Blazor `MiniComposer` regions (visual
 * spec §3): FAB (2.75rem) + bubble (min(23.5rem, viewport − 2rem)) with
 * header / editor / snippets / footer / tail. All classes use the `oc-`
 * prefix and CSS variables only — no global leakage.
 */
import type { ComposerSessionEvent } from "./state.js";
import {
  canSend,
  composerReducer,
  createIdempotencyKey,
  initialComposerState,
  shouldSubmitKey,
  statusTextFor,
  type ComposerMessage,
  type ComposerState,
} from "./state.js";

/** Lower/upper clamp used for bubble placement (spec §6.1). */
export function clamp(value: number, min: number, max: number): number {
  if (Number.isNaN(value)) return min;
  if (min > max) return min;
  if (value < min) return min;
  if (value > max) return max;
  return value;
}

export interface BubbleMetrics {
  /** Bubble width in px: min(376, viewportWidth − 32). */
  widthPx: number;
  /** Bubble max-height in px: min(560, viewportHeight − 120). */
  maxHeightPx: number;
}

/**
 * Pure bubble sizing: `min(23.5rem, viewport − 2rem)` wide
 * (23.5rem = 376px) and at most `min(560px, viewport-height − 120px)` tall.
 */
export function bubbleMetrics(viewportWidth: number, viewportHeight: number): BubbleMetrics {
  return {
    widthPx: Math.min(376, Math.max(0, viewportWidth - 32)),
    maxHeightPx: Math.min(560, Math.max(0, viewportHeight - 120)),
  };
}

/** Minimal structural client surface; satisfied by `OpenConnectClient`. */
export interface ComposerClient {
  createTask(
    input: { clientTaskId: string; sessionId: string; agentId: string; input: string },
    idempotencyKey?: string,
  ): Promise<unknown>;
  subscribeEvents?: (
    sessionId: string,
    handlers: {
      onEvent: (event: ComposerSessionEvent) => void;
      onError?: (error: Error) => void;
      afterSequence?: number;
    },
  ) => () => void;
}

export type ComposerPlacement = "bottom-right" | "bottom-left";

export interface MiniComposerOptions {
  client?: ComposerClient;
  sessionId?: string;
  agentId?: string;
  assistantName?: string;
  placement?: ComposerPlacement;
  suggestions?: readonly string[];
  placeholder?: string;
}

export type ComposerListener = (state: ComposerState) => void;

export interface MiniComposerHandle {
  /** Current immutable state snapshot. */
  getState(): ComposerState;
  /** Subscribe to state changes. Returns an unsubscribe function. */
  subscribe(listener: ComposerListener): () => void;
  /**
   * Renders the FAB + bubble. Defaults to `document.body`. Throws a plain
   * `Error` when no DOM is available (SSR) — importing never throws.
   */
  mount(parent?: HTMLElement): void;
  /** Removes all listeners/observers/subscriptions and DOM nodes. Idempotent. */
  dispose(): void;
  /** Programmatic submit (same path as Enter/Send). */
  send(text?: string): Promise<void>;
  /** Attaches (or re-attaches) the session id, mirroring the Blazor provider flow. */
  attachSession(sessionId: string): void;
  open(): void;
  close(): void;
  toggle(): void;
}

const FAB_SIZE_PX = 44; // 2.75rem visual spec §3

function hasDom(): boolean {
  return typeof document !== "undefined" && typeof window !== "undefined";
}

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className: string,
  text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function friendlyError(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) {
    return /network|fetch|failed/i.test(error.message)
      ? "Could not reach the assistant service. Check your connection and retry."
      : error.message;
  }
  return "Something went wrong sending your message. Try again.";
}

export function createMiniComposer(options: MiniComposerOptions = {}): MiniComposerHandle {
  const assistantName = options.assistantName ?? "Assistant";
  const placement: ComposerPlacement = options.placement ?? "bottom-right";
  const suggestions = [...(options.suggestions ?? [])];
  const placeholder = options.placeholder ?? "Ask the assistant…";
  const client = options.client;
  let sessionId = options.sessionId;
  const agentId = options.agentId ?? "assistant";

  let state: ComposerState = initialComposerState();
  const listeners = new Set<ComposerListener>();
  const notify = (): void => {
    for (const listener of listeners) listener(state);
  };
  const dispatch = (action: Parameters<typeof composerReducer>[1]): void => {
    const next = composerReducer(state, action);
    if (next !== state) {
      state = next;
      notify();
      render();
    }
  };

  // ---- mount-owned resources (created in mount, released in dispose) ----
  let host: HTMLDivElement | null = null;
  let fab: HTMLButtonElement | null = null;
  let bubble: HTMLElement | null = null;
  let statusNode: HTMLParagraphElement | null = null;
  let progressNode: HTMLDivElement | null = null;
  let messagesNode: HTMLDivElement | null = null;
  let errorNode: HTMLDivElement | null = null;
  let errorTextNode: HTMLSpanElement | null = null;
  let editor: HTMLTextAreaElement | null = null;
  let sendButton: HTMLButtonElement | null = null;
  let cancelButton: HTMLButtonElement | null = null;
  let abort: AbortController | null = null;
  let resizeObserver: ResizeObserver | null = null;
  let unsubscribeEvents: (() => void) | null = null;
  let mounted = false;
  /** Last submit text retained so a failed send can be retried verbatim. */
  let lastFailedInput: string | undefined = undefined;

  function applyBubbleBounds(): void {
    if (bubble === null || typeof window === "undefined") return;
    const metrics = bubbleMetrics(window.innerWidth, window.innerHeight);
    bubble.style.width = `${String(metrics.widthPx)}px`;
    bubble.style.maxHeight = `${String(metrics.maxHeightPx)}px`;
  }

  function renderMessage(list: HTMLDivElement, message: ComposerMessage): void {
    const item = el("div", `oc-message oc-message-${message.role}`);
    item.appendChild(el("span", "oc-message-role", message.role === "user" ? "You" : assistantName));
    item.appendChild(el("p", "oc-message-text", message.text));
    list.appendChild(item);
  }

  function render(): void {
    if (!mounted) return;
    const current = state;
    if (fab !== null) {
      const open = current.displayState === "open";
      fab.setAttribute("aria-label", open ? `Close ${assistantName}` : `Open ${assistantName}`);
      fab.setAttribute("aria-expanded", open ? "true" : "false");
    }
    if (bubble !== null) {
      bubble.hidden = current.displayState !== "open";
    }
    if (statusNode !== null) {
      statusNode.textContent = statusTextFor(current, sessionId);
    }
    const loading = current.taskPhase === "running" || current.taskPhase === "queued";
    if (progressNode !== null) {
      progressNode.hidden = !loading;
    }
    if (messagesNode !== null) {
      while (messagesNode.firstChild !== null) messagesNode.removeChild(messagesNode.firstChild);
      if (current.messages.length === 0 && current.streamingText.length === 0) {
        messagesNode.appendChild(
          el("p", "oc-composer-empty", "Ask a question to start. Replies stream here."),
        );
      } else {
        for (const message of current.messages) renderMessage(messagesNode, message);
        if (current.streamingText.length > 0) {
          renderMessage(
            messagesNode,
            { role: "assistant", text: current.streamingText, sequence: current.lastSequence, state: "streaming" },
          );
        }
        messagesNode.scrollTop = messagesNode.scrollHeight;
      }
    }
    if (errorNode !== null && errorTextNode !== null) {
      const hasError = (current.lastError ?? "").length > 0;
      errorNode.hidden = !hasError;
      errorTextNode.textContent = current.lastError ?? "";
    }
    if (editor !== null) {
      editor.disabled = loading;
    }
    if (sendButton !== null) {
      sendButton.disabled = !canSend(
        { ...current, draft: editor?.value ?? current.draft },
        sessionId,
      );
    }
    if (cancelButton !== null) {
      cancelButton.hidden = !loading;
    }
  }

  function onEditorInput(): void {
    if (editor === null) return;
    // Explicit user edit supersedes the retained failed submit.
    lastFailedInput = undefined;
    dispatch({ type: "draft-changed", draft: editor.value });
  }

  function onEditorKeydown(event: KeyboardEvent): void {
    if (
      shouldSubmitKey({
        key: event.key,
        shiftKey: event.shiftKey,
        isComposing: event.isComposing,
        repeat: event.repeat,
        ctrlKey: event.ctrlKey,
        metaKey: event.metaKey,
      })
    ) {
      event.preventDefault();
      void handle.send();
    }
  }

  function onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key === "Escape" && state.displayState === "open") {
      handle.close();
      fab?.focus();
    }
  }

  function onViewportChange(): void {
    applyBubbleBounds();
  }

  function subscribeToEvents(): void {
    try {
      unsubscribeEvents?.();
    } catch {
      // Re-subscription must never throw.
    }
    unsubscribeEvents = null;
    const sid = sessionId;
    if (client?.subscribeEvents !== undefined && (sid ?? "").trim().length > 0) {
      try {
        unsubscribeEvents = client.subscribeEvents(sid as string, {
          onEvent: (event: ComposerSessionEvent) => dispatch({ type: "apply-event", event }),
          onError: (error: Error) => dispatch({ type: "mark-error", message: error.message }),
          afterSequence: state.lastSequence,
        });
      } catch (error) {
        dispatch({ type: "mark-error", message: friendlyError(error) });
      }
    }
  }

  const handle: MiniComposerHandle = {
    getState: () => state,

    subscribe: (listener: ComposerListener): (() => void) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },

    open: (): void => {
      dispatch({ type: "open" });
      editor?.focus();
    },

    close: (): void => {
      dispatch({ type: "close" });
    },

    toggle: (): void => {
      const willOpen = state.displayState !== "open";
      dispatch({ type: "toggle" });
      if (willOpen) editor?.focus();
      else fab?.focus();
    },

    send: async (text?: string): Promise<void> => {
      const value = (text ?? editor?.value ?? state.draft).trim();
      if (value.length === 0) return;
      if (state.taskPhase === "running" || state.taskPhase === "queued") return;
      const sid = sessionId;
      if ((sid ?? "").trim().length === 0) {
        dispatch({ type: "mark-error", message: "No session. Attach a session before sending." });
        return;
      }
      if (client === undefined) {
        dispatch({ type: "mark-error", message: "No client. Provide a client to send messages." });
        return;
      }
      const idempotencyKey = createIdempotencyKey();
      dispatch({ type: "begin-send", text: value, idempotencyKey });
      if (editor !== null) editor.value = "";
      try {
        await client.createTask(
          { clientTaskId: idempotencyKey, sessionId: sid as string, agentId, input: value },
          idempotencyKey,
        );
        // Accepted: the retained failed submit (if any) no longer applies.
        lastFailedInput = undefined;
      } catch (error) {
        // Retain a copy: begin-send already cleared the draft/editor.
        lastFailedInput = value;
        dispatch({ type: "mark-error", message: friendlyError(error) });
      }
    },

    mount: (parent?: HTMLElement): void => {
      if (mounted) return;
      if (!hasDom()) {
        throw new Error("@openconnect/composer mount() requires a DOM (document/window).");
      }
      const container = parent ?? document.body;
      abort = new AbortController();
      const { signal } = abort;

      host = el("div", "oc-composer-host");
      host.dataset["placement"] = placement;

      fab = document.createElement("button");
      fab.type = "button";
      fab.className = "oc-composer-fab";
      fab.style.width = `${String(FAB_SIZE_PX)}px`;
      fab.style.height = `${String(FAB_SIZE_PX)}px`;
      fab.setAttribute("aria-label", `Open ${assistantName}`);
      fab.setAttribute("aria-expanded", "false");
      fab.textContent = "✦";
      fab.addEventListener("click", () => handle.toggle(), { signal });

      bubble = el("section", "oc-composer-bubble");
      bubble.setAttribute("role", "dialog");
      bubble.setAttribute("aria-label", `${assistantName} composer`);
      bubble.setAttribute("aria-modal", "false");
      bubble.hidden = true;

      const header = el("header", "oc-composer-header");
      const titleWrap = el("div", "oc-composer-titles");
      titleWrap.appendChild(el("h2", "oc-composer-title", assistantName));
      statusNode = el("p", "oc-composer-subtitle", statusTextFor(state, sessionId));
      titleWrap.appendChild(statusNode);
      header.appendChild(titleWrap);
      const closeButton = document.createElement("button");
      closeButton.type = "button";
      closeButton.className = "oc-composer-close";
      closeButton.setAttribute("aria-label", `Close ${assistantName}`);
      closeButton.textContent = "✕";
      closeButton.addEventListener("click", () => handle.close(), { signal });
      header.appendChild(closeButton);
      bubble.appendChild(header);

      progressNode = el("div", "oc-composer-progress");
      progressNode.setAttribute("role", "status");
      progressNode.setAttribute("aria-label", "Waiting for reply");
      progressNode.hidden = true;
      bubble.appendChild(progressNode);

      messagesNode = el("div", "oc-composer-messages");
      messagesNode.setAttribute("role", "log");
      messagesNode.setAttribute("aria-live", "polite");
      messagesNode.setAttribute("aria-label", "Conversation");
      bubble.appendChild(messagesNode);

      errorNode = el("div", "oc-composer-error");
      errorNode.setAttribute("role", "alert");
      errorNode.hidden = true;
      errorTextNode = el("span", "oc-composer-error-text", "");
      errorNode.appendChild(errorTextNode);
      const retryButton = document.createElement("button");
      retryButton.type = "button";
      retryButton.className = "oc-composer-retry";
      retryButton.textContent = "Retry";
      retryButton.addEventListener(
        "click",
        () => {
          const currentDraft = (editor?.value ?? state.draft).trim();
          const retryText =
            currentDraft.length > 0 ? currentDraft : lastFailedInput;
          dispatch({ type: "clear-error" });
          void handle.send(retryText);
        },
        { signal },
      );
      errorNode.appendChild(retryButton);
      bubble.appendChild(errorNode);

      if (suggestions.length > 0) {
        const chips = el("div", "oc-composer-chips");
        chips.setAttribute("aria-label", "Suggestions");
        for (const suggestion of suggestions) {
          const chip = document.createElement("button");
          chip.type = "button";
          chip.className = "oc-composer-chip";
          chip.textContent = suggestion;
          chip.addEventListener("click", () => void handle.send(suggestion), { signal });
          chips.appendChild(chip);
        }
        bubble.appendChild(chips);
      }

      const inputRow = el("div", "oc-composer-inputrow");
      const label = document.createElement("label");
      label.className = "oc-composer-label";
      label.textContent = "Message";
      editor = document.createElement("textarea");
      editor.className = "oc-composer-editor";
      editor.rows = 2;
      editor.maxLength = 2000;
      editor.placeholder = placeholder;
      editor.setAttribute("aria-label", `Message to ${assistantName}`);
      const editorId = `oc-composer-editor-${createIdempotencyKey().slice(0, 8)}`;
      editor.id = editorId;
      label.htmlFor = editorId;
      editor.addEventListener("input", onEditorInput, { signal });
      editor.addEventListener("keydown", onEditorKeydown, { signal });
      inputRow.appendChild(label);
      inputRow.appendChild(editor);

      const actions = el("div", "oc-composer-actions");
      cancelButton = document.createElement("button");
      cancelButton.type = "button";
      cancelButton.className = "oc-composer-cancel";
      cancelButton.textContent = "Cancel";
      cancelButton.hidden = true;
      cancelButton.addEventListener("click", () => dispatch({ type: "cancel-streaming" }), {
        signal,
      });
      sendButton = document.createElement("button");
      sendButton.type = "button";
      sendButton.className = "oc-composer-send";
      sendButton.textContent = "Send";
      sendButton.disabled = true;
      sendButton.addEventListener("click", () => void handle.send(), { signal });
      actions.appendChild(cancelButton);
      actions.appendChild(sendButton);
      inputRow.appendChild(actions);
      bubble.appendChild(inputRow);

      const footer = el(
        "footer",
        "oc-composer-footer",
        "Enter to send · Shift+Enter for a new line",
      );
      bubble.appendChild(footer);

      const tail = el("span", "oc-composer-tail");
      tail.setAttribute("aria-hidden", "true");
      bubble.appendChild(tail);

      host.appendChild(fab);
      host.appendChild(bubble);
      container.appendChild(host);
      mounted = true;

      document.addEventListener("keydown", onDocumentKeydown, { signal });
      if (typeof window.addEventListener === "function") {
        window.addEventListener("resize", onViewportChange, { signal });
      }
      const viewport = (window as Window & { visualViewport?: VisualViewport }).visualViewport;
      if (viewport !== undefined && viewport !== null) {
        viewport.addEventListener("resize", onViewportChange, { signal });
      }
      if (typeof ResizeObserver !== "undefined") {
        resizeObserver = new ResizeObserver(onViewportChange);
        resizeObserver.observe(bubble);
      }
      applyBubbleBounds();

      subscribeToEvents();

      render();
    },

    attachSession: (nextId: string): void => {
      sessionId = nextId;
      subscribeToEvents();
      render();
    },

    dispose: (): void => {
      if (!mounted && abort === null && host === null) return;
      try {
        unsubscribeEvents?.();
      } catch {
        // Disposal must never throw.
      }
      unsubscribeEvents = null;
      try {
        resizeObserver?.disconnect();
      } catch {
        // Disposal must never throw.
      }
      resizeObserver = null;
      try {
        abort?.abort();
      } catch {
        // Disposal must never throw.
      }
      abort = null;
      if (host?.parentNode !== null && host?.parentNode !== undefined) {
        host?.parentNode.removeChild(host as Node);
      }
      host = null;
      fab = null;
      bubble = null;
      statusNode = null;
      progressNode = null;
      messagesNode = null;
      errorNode = null;
      errorTextNode = null;
      editor = null;
      sendButton = null;
      cancelButton = null;
      listeners.clear();
      mounted = false;
    },
  };

  return handle;
}
