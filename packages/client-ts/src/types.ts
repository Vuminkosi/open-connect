/**
 * Versioned OpenConnect v1 shared types.
 * Mirrors contracts/openapi/openconnect.v1.yaml (read-only) and the
 * contracts/jsonschema descriptors. Wire enums are kebab-case where the
 * contract uses kebab-case (error codes); task states / capability
 * kinds / states use the exact contract spellings.
 */

/** Wire schema version for every OpenConnect v1 payload. */
export const OPENCONNECT_SCHEMA_VERSION = "openconnect.v1" as const;
export type OpenConnectSchemaVersion = typeof OPENCONNECT_SCHEMA_VERSION;

/** kebab-case stable error codes (see repo AGENTS.md error handling). */
export type OpenConnectErrorCode =
  | "task_not_found"
  | "session_not_found"
  | "unauthorized"
  | "tool_not_allowed"
  | "approval_required"
  | "capability_unavailable"
  | "privacy_blocked"
  | "invalid_request"
  | "conflict"
  | "rate_limited"
  | "legacy_event_without_identity"
  | "unprocessable_entity";

/** Task lifecycle states per POST /tasks 202 response. */
export type TaskState =
  | "queued"
  | "running"
  | "completed"
  | "failed"
  | "cancelled";

/** Capability kinds per contracts/jsonschema/capability.json. */
export type CapabilityKind =
  | "chat"
  | "embedding"
  | "image"
  | "audio"
  | "classification"
  | "security-scan"
  | "tool";

/** Capability states per contracts/jsonschema/capability.json. */
export type CapabilityState =
  | "disabled"
  | "loading"
  | "ready"
  | "degraded"
  | "failed";

export interface CreateTaskRequest {
  schemaVersion: OpenConnectSchemaVersion;
  clientTaskId: string;
  sessionId: string;
  agentId: string;
  input: string;
}

export interface TaskRecord {
  schemaVersion: OpenConnectSchemaVersion;
  taskId: string;
  state: TaskState;
}

export interface Capability {
  schemaVersion: OpenConnectSchemaVersion;
  id: string;
  kind: CapabilityKind;
  state: CapabilityState;
  version: string;
  localOnly?: boolean;
}

export interface SessionEvent {
  schemaVersion: OpenConnectSchemaVersion;
  eventId: string;
  /** Dotted event name, e.g. "task.completed". */
  eventType: string;
  sessionId: string;
  sequence: number;
  occurredAt: string;
  correlationId?: string | null;
  payload: Record<string, unknown>;
}

export interface ErrorBody {
  /** kebab-case machine-readable code. */
  code: OpenConnectErrorCode;
  message: string;
  correlationId?: string;
}
