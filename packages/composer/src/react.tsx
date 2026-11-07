/**
 * Optional React adapter over the framework-neutral composer state machine.
 *
 * `OpenConnectProvider` supplies the client/session context (no duplicate
 * core client is created here) and `MiniComposer` renders the same anchored
 * FAB + bubble regions as the Blazor composer and `createMiniComposer()`.
 *
 * SSR-safe: importing this module touches no DOM. All DOM effects live
 * inside `useEffect`, so server rendering produces static markup and
 * hydration attaches behaviour on the client.
 */
import {
  createContext,
  createElement,
  Fragment,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useReducer,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type { ComposerClient } from "./composer.js";
import {
  canSend,
  composerReducer,
  createIdempotencyKey,
  initialComposerState,
  shouldSubmitKey,
  statusTextFor,
  type ComposerSessionEvent,
} from "./state.js";

export interface OpenConnectContextValue {
  client?: ComposerClient;
  sessionId?: string;
  agentId: string;
}

const OpenConnectContext = createContext<OpenConnectContextValue>({ agentId: "assistant" });

export interface OpenConnectProviderProps {
  client?: ComposerClient;
  sessionId?: string;
  agentId?: string;
  children?: ReactNode;
}

export function OpenConnectProvider(props: OpenConnectProviderProps): ReactNode {
  const value = useMemo<OpenConnectContextValue>(
    () => ({
      ...(props.client === undefined ? {} : { client: props.client }),
      ...(props.sessionId === undefined ? {} : { sessionId: props.sessionId }),
      agentId: props.agentId ?? "assistant",
    }),
    [props.client, props.sessionId, props.agentId],
  );
  return createElement(OpenConnectContext.Provider, { value }, props.children);
}

export interface MiniComposerProps {
  assistantName?: string;
  suggestions?: readonly string[];
  placeholder?: string;
}

function friendlyError(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) return error.message;
  return "Something went wrong sending your message. Try again.";
}

export function MiniComposer(props: MiniComposerProps): ReactNode {
  const assistantName = props.assistantName ?? "Assistant";
  const suggestions = props.suggestions ?? [];
  const placeholder = props.placeholder ?? "Ask the assistant…";
  const { client, sessionId, agentId } = useContext(OpenConnectContext);
  const [state, dispatch] = useReducer(composerReducer, undefined, initialComposerState);
  const [draft, setDraft] = useState("");
  const editorRef = useRef<HTMLTextAreaElement | null>(null);

  const open = state.displayState === "open";
  const loading = state.taskPhase === "running" || state.taskPhase === "queued";

  const send = useCallback(
    async (raw?: string): Promise<void> => {
      const value = (raw ?? draft).trim();
      if (value.length === 0 || loading) return;
      if ((sessionId ?? "").trim().length === 0) {
        dispatch({ type: "mark-error", message: "No session. Attach a session before sending." });
        return;
      }
      if (client === undefined) {
        dispatch({ type: "mark-error", message: "No client. Provide a client to send messages." });
        return;
      }
      const idempotencyKey = createIdempotencyKey();
      dispatch({ type: "begin-send", text: value, idempotencyKey });
      setDraft("");
      try {
        await client.createTask(
          { clientTaskId: idempotencyKey, sessionId: sessionId as string, agentId, input: value },
          idempotencyKey,
        );
      } catch (error) {
        dispatch({ type: "mark-error", message: friendlyError(error) });
      }
    },
    [draft, loading, sessionId, client, agentId],
  );

  // Live events: client-only subscription, cleaned up on unmount/session change.
  useEffect(() => {
    if (client?.subscribeEvents === undefined || (sessionId ?? "").trim().length === 0) return;
    let cancelled = false;
    let unsubscribe: (() => void) | undefined;
    try {
      unsubscribe = client.subscribeEvents(sessionId as string, {
        onEvent: (event: ComposerSessionEvent) => {
          if (!cancelled) dispatch({ type: "apply-event", event });
        },
        onError: (error: Error) => {
          if (!cancelled) dispatch({ type: "mark-error", message: error.message });
        },
        afterSequence: 0,
      });
    } catch (error) {
      dispatch({ type: "mark-error", message: friendlyError(error) });
    }
    return () => {
      cancelled = true;
      try {
        unsubscribe?.();
      } catch {
        // Unmount cleanup must never throw.
      }
    };
  }, [client, sessionId]);

  // Autofocus the editor when the bubble opens (client-only).
  useEffect(() => {
    if (open) editorRef.current?.focus();
  }, [open ]);

  const status = statusTextFor(state, sessionId);
  const canSubmit =
    canSend({ ...state, draft }, sessionId) && !loading && draft.trim().length > 0;

  return createElement(
    "div",
    { className: "oc-composer-host" },
    createElement(
      "button",
      {
        type: "button",
        className: "oc-composer-fab",
        "aria-label": open ? `Close ${assistantName}` : `Open ${assistantName}`,
        "aria-expanded": open ? "true" : "false",
        onClick: () => dispatch({ type: "toggle" }),
      },
      "✦",
    ),
    open
      ? createElement(
          "section",
          {
            className: "oc-composer-bubble",
            role: "dialog",
            "aria-label": `${assistantName} composer`,
            "aria-modal": "false",
          },
          createElement(
            "header",
            { className: "oc-composer-header" },
            createElement(
              "div",
              { className: "oc-composer-titles" },
              createElement("h2", { className: "oc-composer-title" }, assistantName),
              createElement("p", { className: "oc-composer-subtitle" }, status),
            ),
            createElement(
              "button",
              {
                type: "button",
                className: "oc-composer-close",
                "aria-label": `Close ${assistantName}`,
                onClick: () => dispatch({ type: "close" }),
              },
              "✕",
            ),
          ),
          loading
            ? createElement("div", {
                className: "oc-composer-progress",
                role: "status",
                "aria-label": "Waiting for reply",
              })
            : null,
          createElement(
            "div",
            { className: "oc-composer-messages", role: "log", "aria-live": "polite", "aria-label": "Conversation" },
            state.messages.length === 0 && state.streamingText.length === 0
              ? createElement(
                  "p",
                  { className: "oc-composer-empty" },
                  "Ask a question to start. Replies stream here.",
                )
              : createElement(
                  Fragment,
                  null,
                  ...state.messages.map((message, index) =>
                    createElement(
                      "div",
                      {
                        key: `${String(message.sequence)}-${String(index)}`,
                        className: `oc-message oc-message-${message.role}`,
                      },
                      createElement(
                        "span",
                        { className: "oc-message-role" },
                        message.role === "user" ? "You" : assistantName,
                      ),
                      createElement("p", { className: "oc-message-text" }, message.text),
                    ),
                  ),
                  state.streamingText.length > 0
                    ? createElement(
                        "div",
                        { key: "streaming", className: "oc-message oc-message-assistant" },
                        createElement("span", { className: "oc-message-role" }, assistantName),
                        createElement("p", { className: "oc-message-text" }, state.streamingText),
                      )
                    : null,
                ),
          ),
          (state.lastError ?? "").length > 0
            ? createElement(
                "div",
                { className: "oc-composer-error", role: "alert" },
                createElement("span", { className: "oc-composer-error-text" }, state.lastError ?? ""),
                createElement(
                  "button",
                  {
                    type: "button",
                    className: "oc-composer-retry",
                    onClick: () => {
                      dispatch({ type: "clear-error" });
                    },
                  },
                  "Retry",
                ),
              )
            : null,
          suggestions.length > 0
            ? createElement(
                "div",
                { className: "oc-composer-chips", "aria-label": "Suggestions" },
                ...suggestions.map((suggestion) =>
                  createElement(
                    "button",
                    {
                      key: suggestion,
                      type: "button",
                      className: "oc-composer-chip",
                      onClick: () => void send(suggestion),
                    },
                    suggestion,
                  ),
                ),
              )
            : null,
          createElement(
            "div",
            { className: "oc-composer-inputrow" },
            createElement(
              "label",
              { className: "oc-composer-label", htmlFor: "oc-composer-editor-react" },
              "Message",
            ),
            createElement("textarea", {
              ref: editorRef,
              id: "oc-composer-editor-react",
              className: "oc-composer-editor",
              rows: 2,
              maxLength: 2000,
              placeholder,
              disabled: loading,
              "aria-label": `Message to ${assistantName}`,
              value: draft,
              onChange: (event: { target: { value: string } }) => {
                setDraft(event.target.value);
              },
              onKeyDown: (event: {
                key: string;
                shiftKey: boolean;
                ctrlKey?: boolean;
                metaKey?: boolean;
                isComposing?: boolean;
                repeat?: boolean;
                preventDefault: () => void;
              }) => {
                if (
                  shouldSubmitKey({
                    key: event.key,
                    shiftKey: event.shiftKey,
                    isComposing: event.isComposing ?? false,
                    repeat: event.repeat ?? false,
                    ctrlKey: event.ctrlKey,
                    metaKey: event.metaKey,
                  })
                ) {
                  event.preventDefault();
                  void send();
                }
              },
            }),
            createElement(
              "div",
              { className: "oc-composer-actions" },
              loading
                ? createElement(
                    "button",
                    {
                      type: "button",
                      className: "oc-composer-cancel",
                      onClick: () => dispatch({ type: "cancel-streaming" }),
                    },
                    "Cancel",
                  )
                : null,
              createElement(
                "button",
                {
                  type: "button",
                  className: "oc-composer-send",
                  disabled: !canSubmit,
                  onClick: () => void send(),
                },
                "Send",
              ),
            ),
          ),
          createElement(
            "footer",
            { className: "oc-composer-footer" },
            "Enter to send · Shift+Enter for a new line",
          ),
          createElement("span", { className: "oc-composer-tail", "aria-hidden": "true" }),
        )
      : null,
  );
}
