import type {
  Capability,
  CreateTaskRequest,
  OpenConnectErrorCode,
  SessionEvent,
  TaskRecord,
} from "./types.js";
import { OPENCONNECT_SCHEMA_VERSION } from "./types.js";

/**
 * Typed error raised for non-2xx OpenConnect HTTP responses and for
 * client-side transport failures. `code` is always kebab-case.
 */
export class OpenConnectError extends Error {
  readonly code: OpenConnectErrorCode;
  readonly status: number;
  readonly correlationId?: string;
  readonly retryable: boolean;

  constructor(
    code: OpenConnectErrorCode,
    message: string,
    options?: { status?: number; correlationId?: string; retryable?: boolean },
  ) {
    super(message);
    this.name = "OpenConnectError";
    this.code = code;
    this.status = options?.status ?? 0;
    if (options?.correlationId !== undefined) {
      this.correlationId = options.correlationId;
    }
    this.retryable = options?.retryable ?? false;
  }
}

/** Minimal fetch surface the client depends on (browser + Node 24 global fetch). */
export type FetchImpl = (
  input: string | URL,
  init?: RequestInit,
) => Promise<Response>;

/** Minimal WebSocket surface the client depends on (browser + Node 24). */
export interface EventSocket {
  addEventListener(type: "message" | "error" | "close", listener: (ev: unknown) => void): void;
  close(): void;
}

export type WebSocketFactory = (url: string, protocols?: string | string[]) => EventSocket;

export interface OpenConnectClientOptions {
  /** e.g. "https://host/openconnect/v1". Trailing slash is stripped. */
  baseUrl?: string;
  /** Normalized alias for baseUrl matching the registration spec. */
  apiBaseUrl?: string;
  /** Explicit WebSocket endpoint or base URL (e.g. "wss://example.test/openconnect/v1/events"). */
  eventUrl?: string;
  /** Bearer token sent as `Authorization: Bearer <token>` when set. */
  token?: string;
  /** Override for tests / custom transports. Defaults to globalThis.fetch. */
  fetchImpl?: FetchImpl;
  /** Override for tests. Defaults to globalThis.WebSocket. */
  websocketImpl?: WebSocketFactory;
  defaultHeaders?: Record<string, string>;
}

export interface CreateTaskInput {
  clientTaskId: string;
  sessionId: string;
  agentId: string;
  input: string;
}

const IDEMPOTENCY_HEADER = "Idempotency-Key";

/** Problem-type slug for stored events lacking genuine canonical identity. */
const LEGACY_PROBLEM_SLUG = "legacy-event-without-canonical-identity";

/** All-zeros UUID-D: the string equivalent of an empty session id. */
const EMPTY_UUID_D = "00000000-0000-0000-0000-000000000000";

function statusToCode(
  status: number,
  bodyCode?: string,
  options?: { path?: string | undefined; bodyType?: string | undefined },
): OpenConnectErrorCode {
  if (bodyCode !== undefined) {
    const known: OpenConnectErrorCode[] = [
      "task_not_found",
      "session_not_found",
      "unauthorized",
      "tool_not_allowed",
      "approval_required",
      "capability_unavailable",
      "privacy_blocked",
      "invalid_request",
      "conflict",
      "rate_limited",
      "legacy_event_without_identity",
      "unprocessable_entity",
    ];
    if ((known as string[]).includes(bodyCode)) return bodyCode as OpenConnectErrorCode;
  }
  if (options?.bodyType?.includes(LEGACY_PROBLEM_SLUG) === true) {
    return "legacy_event_without_identity";
  }
  switch (status) {
    case 401:
      return "unauthorized";
    case 404:
      return options?.path?.startsWith("/sessions/") === true
        ? "session_not_found"
        : "task_not_found";
    case 409:
      return "conflict";
    case 422:
      return "unprocessable_entity";
    case 429:
      return "rate_limited";
    default:
      return "invalid_request";
  }
}

/** Pre-fetch guard for event replay/subscribe: non-empty session id, non-negative integer cursor. */
function assertEventCursor(sessionId: string, afterSequence: number): void {
  const trimmed = sessionId.trim();
  if (trimmed.length === 0 || trimmed === EMPTY_UUID_D) {
    throw new OpenConnectError("invalid_request", "sessionId must be a non-empty session id.", {
      retryable: false,
    });
  }
  if (!Number.isInteger(afterSequence) || afterSequence < 0) {
    throw new OpenConnectError(
      "invalid_request",
      "afterSequence must be a non-negative integer.",
      { retryable: false },
    );
  }
}

/**
 * Fetch-based SDK for the OpenConnect v1 HTTP contract.
 * Speaks only HTTP/WebSocket; it never exposes NATS to consumers.
 */
export class OpenConnectClient {
  private readonly baseUrl: string;
  private readonly eventUrl?: string;
  private readonly token?: string;
  private readonly fetchImpl: FetchImpl;
  private readonly websocketImpl?: WebSocketFactory;
  private readonly defaultHeaders: Record<string, string>;

  constructor(options: OpenConnectClientOptions) {
    const rawUrl = options.baseUrl ?? options.apiBaseUrl;
    if (!rawUrl || rawUrl.trim().length === 0) {
      throw new OpenConnectError("invalid_request", "baseUrl (or apiBaseUrl) must not be empty.");
    }
    this.baseUrl = rawUrl.replace(/\/+$/, "");
    if (options.eventUrl !== undefined && options.eventUrl.trim().length > 0) {
      this.eventUrl = options.eventUrl.trim().replace(/\/+$/, "");
    }
    if (options.token !== undefined) this.token = options.token;
    const configuredFetch = options.fetchImpl ?? (globalThis.fetch as FetchImpl | undefined);
    if (configuredFetch === undefined) {
      throw new OpenConnectError(
        "invalid_request",
        "No fetch implementation available. Provide options.fetchImpl.",
      );
    }
    this.fetchImpl = configuredFetch.bind(globalThis);
    if (options.websocketImpl !== undefined) {
      this.websocketImpl = options.websocketImpl;
    } else {
      const Ws = (globalThis as Record<string, unknown>)["WebSocket"] as
        | (new (url: string, protocols?: string | string[]) => EventSocket)
        | undefined;
      if (Ws !== undefined) {
        this.websocketImpl = (url, protocols) => new Ws(url, protocols);
      }
    }
    this.defaultHeaders = { ...(options.defaultHeaders ?? {}) };
  }

  /** POST /tasks — accept a task. `idempotencyKey` defaults to `clientTaskId`. */
  async createTask(input: CreateTaskInput, idempotencyKey?: string): Promise<TaskRecord> {
    const body: CreateTaskRequest = {
      schemaVersion: OPENCONNECT_SCHEMA_VERSION,
      ...input,
    };
    return this.request<TaskRecord>("/tasks", {
      method: "POST",
      headers: { [IDEMPOTENCY_HEADER]: idempotencyKey ?? input.clientTaskId },
      body: JSON.stringify(body),
    });
  }

  /** GET /tasks/{taskId} — fetch a task record. */
  async getTask(taskId: string): Promise<TaskRecord> {
    return this.request<TaskRecord>(`/tasks/${encodeURIComponent(taskId)}`);
  }

  /** POST /tasks/{taskId}/cancel — request task cancellation. */
  async cancelTask(taskId: string): Promise<TaskRecord> {
    return this.request<TaskRecord>(`/tasks/${encodeURIComponent(taskId)}/cancel`, {
      method: "POST",
    });
  }

  /** GET /capabilities — list advertised capabilities. */
  async listCapabilities(): Promise<Capability[]> {
    return this.request<Capability[]>("/capabilities");
  }

  /** GET /sessions/{sessionId}/events?afterSequence=n — replay events. */
  async replayEvents(sessionId: string, afterSequence = 0): Promise<SessionEvent[]> {
    assertEventCursor(sessionId, afterSequence);
    const path =
      `/sessions/${encodeURIComponent(sessionId)}/events` +
      `?afterSequence=${encodeURIComponent(String(afterSequence))}`;
    return this.request<SessionEvent[]>(path);
  }

  /**
   * Subscribe to live session events over WebSocket.
   * Resolves with an unsubscribe function. Requires a WebSocket implementation.
   */
  subscribeEvents(
    sessionId: string,
    handlers: {
      onEvent: (event: SessionEvent) => void;
      onError?: (error: OpenConnectError) => void;
      afterSequence?: number;
    },
  ): () => void {
    const after = handlers.afterSequence ?? 0;
    assertEventCursor(sessionId, after);
    if (this.websocketImpl === undefined) {
      throw new OpenConnectError(
        "invalid_request",
        "No WebSocket implementation available. Provide options.websocketImpl.",
      );
    }
    const rawWsBase = (this.eventUrl ?? this.baseUrl).replace(/^http/, "ws").replace(/\/+$/, "");
    const baseWithoutEvents = rawWsBase.endsWith("/events")
      ? rawWsBase.slice(0, -"/events".length)
      : rawWsBase;
    const url =
      `${baseWithoutEvents}/sessions/${encodeURIComponent(sessionId)}/events` +
      `?afterSequence=${encodeURIComponent(String(after))}`;
    const socket = this.websocketImpl(url, "openconnect.v1");
    const onMessage = (ev: unknown): void => {
      try {
        const raw =
          typeof ev === "object" && ev !== null && "data" in ev
            ? String((ev as { data: unknown }).data)
            : String(ev);
        handlers.onEvent(JSON.parse(raw) as SessionEvent);
      } catch (error) {
        handlers.onError?.(
          error instanceof OpenConnectError
            ? error
            : new OpenConnectError("invalid_request", "Failed to parse session event."),
        );
      }
    };
    const onError = (): void => {
      handlers.onError?.(new OpenConnectError("invalid_request", "Session event stream error."));
    };
    socket.addEventListener("message", onMessage);
    socket.addEventListener("error", onError);
    return () => socket.close();
  }

  private async request<T>(path: string, init?: RequestInit): Promise<T> {
    const headers: Record<string, string> = {
      Accept: "application/json",
      ...this.defaultHeaders,
      ...(init?.headers as Record<string, string> | undefined),
    };
    if (init?.body !== undefined) headers["Content-Type"] = "application/json";
    if (this.token !== undefined) headers["Authorization"] = `Bearer ${this.token}`;

    let response: Response;
    try {
      response = await this.fetchImpl(`${this.baseUrl}${path}`, { ...init, headers });
    } catch {
      throw new OpenConnectError("invalid_request", "Network request failed.", {
        retryable: true,
      });
    }
    if (!response.ok) {
      throw await this.toError(response, path);
    }
    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  private async toError(response: Response, path?: string): Promise<OpenConnectError> {
    let code: string | undefined;
    let bodyType: string | undefined;
    let message = `Request failed with status ${String(response.status)}.`;
    let correlationId: string | undefined;
    try {
      const body = (await response.json()) as Partial<{
        code: string;
        type: string;
        message: string;
        title: string;
        correlationId: string;
      }>;
      if (typeof body.code === "string") code = body.code;
      if (typeof body.type === "string") bodyType = body.type;
      if (typeof body.message === "string" && body.message.length > 0) {
        message = body.message;
      } else if (typeof body.title === "string" && body.title.length > 0) {
        message = body.title;
      }
      if (typeof body.correlationId === "string") correlationId = body.correlationId;
    } catch {
      // Non-JSON error body: fall back to status-based mapping.
    }
    return new OpenConnectError(statusToCode(response.status, code, { path, bodyType }), message, {
      status: response.status,
      ...(correlationId === undefined ? {} : { correlationId }),
      retryable: response.status === 429 || response.status >= 500,
    });
  }
}

/**
 * Creates an OpenConnectClient instance from configuration options.
 * Conforms to openconnect-registration-spec §3.5.
 */
export function createOpenConnectClient(options: OpenConnectClientOptions): OpenConnectClient {
  return new OpenConnectClient(options);
}
