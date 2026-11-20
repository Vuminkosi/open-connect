import { describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { OpenConnectClient, OpenConnectError } from "./client.js";
import type { CreateTaskInput } from "./client.js";
import type { Capability, SessionEvent, TaskRecord } from "./types.js";

const SESSION_ID = "123e4567-e89b-12d3-a456-426614174000";
const TASK_ULID = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const TENANT_ULID = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const PRINCIPAL_ULID = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function clientWith(fetchImpl: (input: string | URL, init?: RequestInit) => Promise<Response>) {
  return new OpenConnectClient({ baseUrl: "https://api.example.test/openconnect/v1", fetchImpl });
}

describe("createTask", () => {
  it("posts the host task request and returns its ULID record", async () => {
    const record = JSON.parse(readFileSync(new URL("../tests/fixtures/task-record.json", import.meta.url), "utf8")) as TaskRecord;
    const fetchImpl = vi.fn(async () =>
      jsonResponse(202, record),
    );
    const client = clientWith(fetchImpl);
    const task = await client.createTask(
      { clientTaskId: "client-1", sessionId: TASK_ULID, message: "hello" },
      "idem-1",
    );
    expect(task).toEqual(record);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    const call = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    const [url, init] = call;
    expect(url).toBe("https://api.example.test/openconnect/v1/tasks");
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["Idempotency-Key"]).toBe("idem-1");
    expect(JSON.parse(String(init.body))).toEqual({ clientTaskId: "client-1", sessionId: TASK_ULID, message: "hello" });
  });

  it("rejects the obsolete input and agentId shape instead of dropping its prompt", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(202, {}));
    const legacy = { clientTaskId: "client-1", input: "hello", agentId: "agent-a" } as unknown as CreateTaskInput;
    await expect(clientWith(fetchImpl).createTask(legacy)).rejects.toMatchObject({ code: "invalid_request" });
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});

describe("getTask", () => {
  it("returns the task record", async () => {
    const record: TaskRecord = { id: TASK_ULID, tenantId: TENANT_ULID, principalId: PRINCIPAL_ULID, state: "running" };
    const client = clientWith(vi.fn(async () => jsonResponse(200, record)));
    await expect(client.getTask(TASK_ULID)).resolves.toEqual(record);
  });

  it("maps 404 to task_not_found", async () => {
    const client = clientWith(
      vi.fn(async () => jsonResponse(404, { code: "task_not_found", message: "No such task." })),
    );
    const error = await client.getTask("missing").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(OpenConnectError);
    expect((error as OpenConnectError).code).toBe("task_not_found");
    expect((error as OpenConnectError).status).toBe(404);
    expect((error as OpenConnectError).retryable).toBe(false);
  });
});

describe("cancelTask", () => {
  it("posts to the cancel endpoint", async () => {
    const fetchImpl = vi.fn(async () =>
      jsonResponse(200, { id: TASK_ULID, tenantId: TENANT_ULID, principalId: PRINCIPAL_ULID, state: "cancelled" }),
    );
    const client = clientWith(fetchImpl);
    const task = await client.cancelTask(TASK_ULID);
    expect(task.state).toBe("cancelled");
    const cancelCall = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    const [url, init] = cancelCall;
    expect(url).toBe(`https://api.example.test/openconnect/v1/tasks/${TASK_ULID}/cancel`);
    expect(init.method).toBe("POST");
  });
});

describe("listCapabilities", () => {
  it("returns capabilities", async () => {
    const capabilities: Capability[] = [
      { schemaVersion: "openconnect.v1", id: "chat-1", kind: "chat", state: "ready", version: "1.0.0" },
    ];
    const client = clientWith(vi.fn(async () => jsonResponse(200, capabilities)));
    await expect(client.listCapabilities()).resolves.toEqual(capabilities);
  });
});

describe("replayEvents", () => {
  it("requests events after the cursor", async () => {
    const events: SessionEvent[] = [
      {
        schemaVersion: "openconnect.v1",
        eventId: "123e4567-e89b-12d3-a456-426614174002",
        eventType: "task.completed",
        sessionId: SESSION_ID,
        sequence: 1,
        occurredAt: "2026-01-01T00:00:00.000Z",
        payload: {},
      },
    ];
    const fetchImpl = vi.fn(async () => jsonResponse(200, events));
    const client = clientWith(fetchImpl);
    await expect(client.replayEvents(SESSION_ID, 0)).resolves.toEqual(events);
    const replayCall = fetchImpl.mock.calls[0] as unknown as [string, RequestInit?];
    const [url] = replayCall;
    expect(url).toBe(
      `https://api.example.test/openconnect/v1/sessions/${SESSION_ID}/events?afterSequence=0`,
    );
  });

  it("maps 401 to unauthorized", async () => {
    const client = clientWith(
      vi.fn(async () => jsonResponse(401, { code: "unauthorized", message: "Auth required." })),
    );
    const error = await client.replayEvents(SESSION_ID).catch((e: unknown) => e);
    expect((error as OpenConnectError).code).toBe("unauthorized");
  });

  it("maps 422 legacy problem type to legacy_event_without_identity", async () => {
    const client = clientWith(
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              type: "https://openconnect.dev/problems/legacy-event-without-canonical-identity",
              title: "A stored event lacks genuine canonical identity and cannot be projected.",
              status: 422,
              correlationId: "corr-9",
            }),
            { status: 422, headers: { "Content-Type": "application/problem+json" } },
          ),
      ),
    );
    const error = (await client.replayEvents(SESSION_ID).catch((e: unknown) => e)) as OpenConnectError;
    expect(error).toBeInstanceOf(OpenConnectError);
    expect(error.code).toBe("legacy_event_without_identity");
    expect(error.status).toBe(422);
    expect(error.retryable).toBe(false);
    expect(error.correlationId).toBe("corr-9");
  });

  it("maps 422 without a problem body to unprocessable_entity", async () => {
    const client = clientWith(
      vi.fn(async () => new Response("{not-json", { status: 422 })),
    );
    const error = (await client.replayEvents(SESSION_ID).catch((e: unknown) => e)) as OpenConnectError;
    expect(error.code).toBe("unprocessable_entity");
    expect(error.status).toBe(422);
    expect(error.retryable).toBe(false);
  });

  it("maps 404 without a code on the events path to session_not_found", async () => {
    const client = clientWith(
      vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              type: "https://openconnect.dev/problems/session-not-found",
              title: "No such session.",
              status: 404,
            }),
            { status: 404, headers: { "Content-Type": "application/problem+json" } },
          ),
      ),
    );
    const error = (await client.replayEvents(SESSION_ID).catch((e: unknown) => e)) as OpenConnectError;
    expect(error.code).toBe("session_not_found");
    expect(error.status).toBe(404);
    expect(error.retryable).toBe(false);
  });

  it.each([-1, 1.5, Number.NaN])(
    "rejects invalid afterSequence %s before fetch",
    async (afterSequence) => {
      const fetchImpl = vi.fn(async () => jsonResponse(200, []));
      const client = clientWith(fetchImpl);
      const error = (await client
        .replayEvents(SESSION_ID, afterSequence)
        .catch((e: unknown) => e)) as OpenConnectError;
      expect(error).toBeInstanceOf(OpenConnectError);
      expect(error.code).toBe("invalid_request");
      expect(fetchImpl).not.toHaveBeenCalled();
    },
  );

  it.each(["", "   ", "00000000-0000-0000-0000-000000000000"])(
    "rejects empty session id %s before fetch",
    async (sessionId) => {
      const fetchImpl = vi.fn(async () => jsonResponse(200, []));
      const client = clientWith(fetchImpl);
      const error = (await client.replayEvents(sessionId).catch((e: unknown) => e)) as OpenConnectError;
      expect(error).toBeInstanceOf(OpenConnectError);
      expect(error.code).toBe("invalid_request");
      expect(fetchImpl).not.toHaveBeenCalled();
    },
  );
});

describe("subscribeEvents validation", () => {
  it("rejects negative cursor before opening a socket", () => {
    const openSocket = vi.fn();
    const client = new OpenConnectClient({
      baseUrl: "https://api.example.test/openconnect/v1",
      fetchImpl: async () => jsonResponse(200, []),
      websocketImpl: openSocket,
    });
    let error: unknown;
    try {
      client.subscribeEvents(SESSION_ID, { onEvent: () => {}, afterSequence: -1 });
    } catch (e: unknown) {
      error = e;
    }
    expect(error).toBeInstanceOf(OpenConnectError);
    expect((error as OpenConnectError).code).toBe("invalid_request");
    expect(openSocket).not.toHaveBeenCalled();
  });

  it("rejects empty session id before opening a socket", () => {
    const openSocket = vi.fn();
    const client = new OpenConnectClient({
      baseUrl: "https://api.example.test/openconnect/v1",
      fetchImpl: async () => jsonResponse(200, []),
      websocketImpl: openSocket,
    });
    let error: unknown;
    try {
      client.subscribeEvents("", { onEvent: () => {} });
    } catch (e: unknown) {
      error = e;
    }
    expect(error).toBeInstanceOf(OpenConnectError);
    expect((error as OpenConnectError).code).toBe("invalid_request");
    expect(openSocket).not.toHaveBeenCalled();
  });
});
