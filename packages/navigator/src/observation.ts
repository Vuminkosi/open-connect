/**
 * Bounded visible semantic snapshot builder.
 *
 * By construction the snapshot contains only caller-supplied visible text:
 * hidden nodes are dropped, form inputs are represented as redacted fields
 * (their typed values are never collected), and raw HTML, credential
 * stores, browser storage, and cross-frame content are never read at all.
 *
 * Capture is a two-key system: the caller must enable capture
 * (`capture: { enabled: true }`) and the server must grant it. With capture
 * disabled (the default) the builder returns `{ captured: false }` with no
 * nodes, so tours degrade to the static text fallback.
 *
 * This module is DOM-free at import time and SSR-safe. The optional
 * {@link collectVisibleNodes} helper touches the DOM only when invoked with
 * an explicit root element.
 */

/** Wire schema version shared with the OpenConnect v1 contract. */
export const NAVIGATOR_SCHEMA_VERSION = "openconnect.v1" as const;

/** Caller-supplied raw node (already extracted from the view layer). */
export interface RawSnapshotNode {
  /** Stable target id when the node maps to a registered target. */
  targetId?: string;
  /** Semantic role, e.g. "heading", "text", "button", "field". */
  role: string;
  /** Visible label for the node. */
  label: string;
  /** Visible text content (never raw HTML). */
  text?: string;
  /** Hidden nodes are always dropped. */
  hidden?: boolean;
  /** Form inputs are always redacted, never captured verbatim. */
  isInput?: boolean;
  /** Field name used for sensitivity detection. */
  inputName?: string;
  /** Field type used for sensitivity detection (e.g. "password"). */
  inputType?: string;
  /** Typed values are never copied into the snapshot. */
  value?: string;
}

/** Sanitized node safe to transport. */
export interface SnapshotNode {
  role: string;
  label: string;
  text: string;
  redacted: boolean;
  targetId?: string;
}

/** Bounded snapshot returned to the tour/command pipeline. */
export interface VisibleSnapshot {
  schemaVersion: typeof NAVIGATOR_SCHEMA_VERSION;
  routeId: string;
  /** `false` when capture is disabled (default) — no content collected. */
  captured: boolean;
  /** Machine-readable reason: "capture-disabled" | "visible-only". */
  reason: "capture-disabled" | "visible-only";
  nodes: SnapshotNode[];
  /** `true` when node/text bounds truncated the snapshot. */
  truncated: boolean;
}

export interface BuildSnapshotInput {
  routeId: string;
  nodes: RawSnapshotNode[];
  captureEnabled: boolean;
}

/** Snapshot bounds (spec §4.3): bounded by count and per-node text. */
export const SNAPSHOT_MAX_NODES = 100;
export const SNAPSHOT_MAX_TEXT_LENGTH = 200;

/** Placeholder used wherever a value is withheld. */
export const REDACTED_PLACEHOLDER = "[withheld]" as const;

/** Field names (or types) treated as sensitive under NAV-AT-010. */
const SENSITIVE_FIELD_PATTERN =
  /passw|passwd|secret|token|api[-_]?key|ssn|social.security|credit|card|cvv|cvc|ccnum|credential|private.key|pin/i;

/** Returns `true` when a field name/type must be treated as sensitive. */
export function isSensitiveField(inputName: string | undefined, inputType: string | undefined): boolean {
  if (inputType !== undefined && inputType.toLowerCase() === "password") return true;
  if (inputName === undefined) return false;
  return SENSITIVE_FIELD_PATTERN.test(inputName);
}

/** Collapse whitespace and bound visible text to the snapshot limit. */
export function sanitizeNodeText(text: string): string {
  return text.replace(/\s+/g, " ").trim().slice(0, SNAPSHOT_MAX_TEXT_LENGTH);
}

/**
 * Build a bounded visible snapshot. With capture disabled (the default)
 * nothing is collected: `{ captured: false, nodes: [] }`. With capture
 * enabled, hidden nodes are dropped, inputs become redacted fields, and
 * sensitive values are replaced with the withheld placeholder.
 */
export function buildVisibleSnapshot(input: BuildSnapshotInput): VisibleSnapshot {
  if (!input.captureEnabled) {
    return {
      schemaVersion: NAVIGATOR_SCHEMA_VERSION,
      routeId: input.routeId,
      captured: false,
      reason: "capture-disabled",
      nodes: [],
      truncated: false,
    };
  }
  const nodes: SnapshotNode[] = [];
  let truncated = false;
  for (const raw of input.nodes) {
    if (raw.hidden === true) continue;
    if (nodes.length >= SNAPSHOT_MAX_NODES) {
      truncated = true;
      break;
    }
    if (raw.isInput === true) {
      nodes.push({
        role: "field",
        label: sanitizeNodeText(raw.label),
        text: REDACTED_PLACEHOLDER,
        redacted: true,
        ...(raw.targetId === undefined ? {} : { targetId: raw.targetId }),
      });
      continue;
    }
    const sensitive = isSensitiveField(raw.inputName, raw.inputType);
    const text = raw.text === undefined ? "" : sanitizeNodeText(raw.text);
    nodes.push({
      role: raw.role,
      label: sanitizeNodeText(raw.label),
      text: sensitive ? REDACTED_PLACEHOLDER : text,
      redacted: sensitive,
      ...(raw.targetId === undefined ? {} : { targetId: raw.targetId }),
    });
  }
  return {
    schemaVersion: NAVIGATOR_SCHEMA_VERSION,
    routeId: input.routeId,
    captured: true,
    reason: "visible-only",
    nodes,
    truncated,
  };
}

/**
 * Minimal structural element surface used by {@link collectVisibleNodes}.
 * Uses only widely available DOM members so tests can supply fakes.
 */
export interface WalkableElement {
  readonly tagName: string;
  readonly children: ArrayLike<WalkableElement>;
  getAttribute(name: string): string | null;
  readonly textContent: string | null;
}

/**
 * Walk a caller-supplied root element and extract visible semantic nodes.
 * Skips `<script>`/`<style>`, hidden elements (`hidden` attribute,
 * `aria-hidden="true"`, `data-hidden="true"`), all form-control values
 * (inputs become redacted field markers), and never reads raw HTML,
 * storage, or cross-frame content. Bounded to `SNAPSHOT_MAX_NODES`.
 *
 * Call only in the browser after `start()`; returns `[]` for a missing root.
 */
export function collectVisibleNodes(root: WalkableElement | null | undefined): RawSnapshotNode[] {
  if (root === null || root === undefined) return [];
  const out: RawSnapshotNode[] = [];
  const visit = (element: WalkableElement): void => {
    if (out.length >= SNAPSHOT_MAX_NODES) return;
    const tag = element.tagName.toLowerCase();
    if (tag === "script" || tag === "style" || tag === "noscript") return;
    if (
      element.getAttribute("hidden") !== null ||
      element.getAttribute("aria-hidden") === "true" ||
      element.getAttribute("data-hidden") === "true"
    ) {
      return;
    }
    if (tag === "input" || tag === "textarea" || tag === "select") {
      const label =
        element.getAttribute("aria-label") ??
        element.getAttribute("name") ??
        element.getAttribute("placeholder") ??
        tag;
      const inputName = element.getAttribute("name");
      const inputType = element.getAttribute("type");
      const targetId = element.getAttribute("data-navigator-target");
      out.push({
        role: "field",
        label,
        isInput: true,
        ...(inputName === null ? {} : { inputName }),
        ...(inputType === null ? {} : { inputType }),
        ...(targetId === null ? {} : { targetId }),
      });
      return;
    }
    const children = element.children;
    if (children.length === 0) {
      const text = (element.textContent ?? "").replace(/\s+/g, " ").trim();
      if (text.length === 0) return;
      const leafTarget = element.getAttribute("data-navigator-target");
      out.push({
        role: tag === "button" ? "button" : "text",
        label: text.slice(0, SNAPSHOT_MAX_TEXT_LENGTH),
        text: text.slice(0, SNAPSHOT_MAX_TEXT_LENGTH),
        ...(leafTarget === null ? {} : { targetId: leafTarget }),
      });
      return;
    }
    for (let index = 0; index < children.length; index += 1) {
      const child = children[index];
      if (child === undefined) continue;
      visit(child);
      if (out.length >= SNAPSHOT_MAX_NODES) return;
    }
  };
  visit(root);
  return out;
}
