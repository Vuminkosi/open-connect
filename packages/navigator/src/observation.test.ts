import { describe, expect, it } from "vitest";
import {
  REDACTED_PLACEHOLDER,
  buildVisibleSnapshot,
  collectVisibleNodes,
  isSensitiveField,
  sanitizeNodeText,
  type WalkableElement,
} from "./observation.js";

function fakeElement(init: {
  tag?: string;
  text?: string | null;
  attrs?: Record<string, string>;
  children?: WalkableElement[];
}): WalkableElement {
  const attrs = init.attrs ?? {};
  return {
    tagName: (init.tag ?? "div").toUpperCase(),
    children: init.children ?? [],
    getAttribute: (name: string) => attrs[name] ?? null,
    textContent: init.text ?? null,
  };
}

describe("observation sanitizer", () => {
  it("returns an empty uncaptured snapshot when capture is off (default)", () => {
    const snapshot = buildVisibleSnapshot({
      routeId: "/home",
      captureEnabled: false,
      nodes: [{ role: "text", label: "Hello", text: "Hello" }],
    });
    expect(snapshot.captured).toBe(false);
    expect(snapshot.reason).toBe("capture-disabled");
    expect(snapshot.nodes).toEqual([]);
  });

  it("strips hidden nodes and redacts inputs and sensitive fields (NAV-AT-010)", () => {
    const snapshot = buildVisibleSnapshot({
      routeId: "/home",
      captureEnabled: true,
      nodes: [
        { role: "text", label: "Welcome", text: "Welcome" },
        { role: "text", label: "Hidden", text: "Hidden", hidden: true },
        { role: "field", label: "Name", isInput: true, inputName: "name", value: "Ada" },
        { role: "text", label: "Password", text: "s3cret", inputName: "password" },
        { role: "text", label: "Card", text: "4111 1111", inputName: "cardNumber" },
      ],
    });
    expect(snapshot.captured).toBe(true);
    expect(snapshot.nodes.map((n) => n.label)).toEqual(["Welcome", "Name", "Password", "Card"]);
    expect(snapshot.nodes[1]?.text).toBe(REDACTED_PLACEHOLDER);
    expect(snapshot.nodes[2]?.text).toBe(REDACTED_PLACEHOLDER);
    expect(snapshot.nodes[3]?.text).toBe(REDACTED_PLACEHOLDER);
    // Typed values never leak into the snapshot.
    expect(JSON.stringify(snapshot)).not.toContain("Ada");
    expect(JSON.stringify(snapshot)).not.toContain("s3cret");
  });

  it("bounds node count and text length", () => {
    const snapshot = buildVisibleSnapshot({
      routeId: "/home",
      captureEnabled: true,
      nodes: Array.from({ length: 150 }, (_, i) => ({
        role: "text",
        label: `Item ${String(i)}`,
        text: "x".repeat(500),
      })),
    });
    expect(snapshot.nodes).toHaveLength(100);
    expect(snapshot.truncated).toBe(true);
    expect(snapshot.nodes[0]?.text.length).toBeLessThanOrEqual(200);
  });

  it("detects sensitive fields and sanitizes text", () => {
    expect(isSensitiveField("password", "text")).toBe(true);
    expect(isSensitiveField("apiKey", undefined)).toBe(true);
    expect(isSensitiveField("email", "email")).toBe(false);
    expect(sanitizeNodeText("  a   b\nc  ")).toBe("a b c");
  });

  it("collects only visible semantic nodes from a DOM root", () => {
    const root = fakeElement({
      tag: "main",
      children: [
        fakeElement({ tag: "h1", text: "  Welcome  " }),
        fakeElement({ tag: "script", text: "alert(1)" }),
        fakeElement({ tag: "p", text: "Hidden", attrs: { "aria-hidden": "true" } }),
        fakeElement({ tag: "input", attrs: { name: "email", type: "email", value: "a@b.c" } }),
      ],
    });
    const nodes = collectVisibleNodes(root);
    expect(nodes).toHaveLength(2);
    expect(nodes[0]).toMatchObject({ role: "text", label: "Welcome" });
    expect(nodes[1]).toMatchObject({ role: "field", isInput: true });
    expect(JSON.stringify(nodes)).not.toContain("a@b.c");
    expect(collectVisibleNodes(null)).toEqual([]);
  });
});
