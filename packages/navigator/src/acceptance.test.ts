import { describe, expect, it, vi } from "vitest";
import { hasDom, showHighlight } from "./highlighter.js";
import {
  createOpenConnectNavigator,
  renderTextTour,
  TOUR_MAX_STEPS,
  validateTourSteps,
} from "./navigator.js";
import {
  buildVisibleSnapshot,
  REDACTED_PLACEHOLDER,
} from "./observation.js";

/**
 * NAV-CLOSE-03 client acceptance evidence.
 * Maps bridge behavior to NAV-AT-002 (static text tour), NAV-AT-007
 * (SSR-safe import + client/MiniComposer coexistence), and NAV-AT-010
 * (capture denied/redacted by default).
 */

const BASE_URL = "https://tour.example/openconnect/v1";

function makeSteps(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    title: `Step title ${String(i + 1)}`,
    body: `Guidance for step ${String(i + 1)}.`,
  }));
}

describe("NAV-AT-007 SSR-safe import and coexistence", () => {
  it("has no DOM in the Node test environment; highlight degrades to null", () => {
    expect(typeof window).toBe("undefined");
    expect(typeof document).toBe("undefined");
    expect(hasDom()).toBe(false);
    expect(
      showHighlight(
        { title: "T", body: "B", stepIndex: 0, stepTotal: 1 },
        { onNext: () => {}, onBack: () => {}, onSkip: () => {}, onClose: () => {} },
      ),
    ).toBeNull();
  });

  it("creating the bridge touches no DOM globals and never mutates the client", async () => {
    const before = new Set(Object.keys(globalThis));
    const client = Object.freeze({
      listCapabilities: vi.fn(async () => ({ caps: [] })),
    });
    const clientKeysBefore = Object.keys(client);
    const nav = createOpenConnectNavigator({
      baseUrl: BASE_URL,
      client,
      routeId: "/editor",
      fetchImpl: vi.fn(async () => new Response("forbidden", { status: 403 })),
    });
    expect(typeof window).toBe("undefined");
    expect(typeof document).toBe("undefined");
    expect(Object.keys(client)).toEqual(clientKeysBefore);
    expect(client.listCapabilities).toBe(client.listCapabilities);
    const after = new Set(Object.keys(globalThis));
    expect([...after].filter((key) => !before.has(key))).toEqual([]);
    // Frozen (externally owned) client must still work as a probe.
    const started = await nav.start();
    expect(started.started).toBe(true);
    expect(client.listCapabilities).toHaveBeenCalledTimes(1);
    nav.dispose();
  });
});

describe("NAV-AT-002 static text tour with bridge (25-step bound)", () => {
  it("renders a full 25-step text tour in order; 26 steps are rejected", () => {
    expect(TOUR_MAX_STEPS).toBe(25);
    const steps = makeSteps(25);
    expect(validateTourSteps(steps).ok).toBe(true);
    const text = renderTextTour(steps);
    expect(text).toContain("App tour — 25 steps");
    expect(text).toContain("Step 1 of 25: Step title 1");
    expect(text).toContain("Step 25 of 25: Step title 25");
    // Order preserved.
    expect(text.indexOf("Step title 1")).toBeLessThan(text.indexOf("Step title 25"));

    const over = validateTourSteps(makeSteps(26));
    expect(over.ok).toBe(false);
    expect(over.problems.some((p) => p.message.includes("maximum 25"))).toBe(true);
  });

  it("single-step and empty tours render the text fallback", () => {
    expect(renderTextTour([{ title: "Hello", body: "Welcome." }])).toContain(
      "App tour — 1 step",
    );
    expect(renderTextTour([])).toBe("No tour steps available.");
  });

  it("runOverlayTour walks 25 steps in text-only mode with clamped navigation", () => {
    const nav = createOpenConnectNavigator({ baseUrl: BASE_URL });
    const seen: number[] = [];
    const tour = nav.runOverlayTour(makeSteps(25), {
      onStep: (index) => seen.push(index),
    });
    expect(tour.textOnly).toBe(true);
    expect(tour.currentIndex).toBe(0);

    tour.goTo(24);
    expect(tour.currentIndex).toBe(24);
    // Out-of-range indices clamp to the tour bounds.
    tour.goTo(99);
    expect(tour.currentIndex).toBe(24);
    tour.goTo(-5);
    expect(tour.currentIndex).toBe(0);

    tour.goTo(23);
    tour.next();
    expect(tour.currentIndex).toBe(24);
    tour.back();
    expect(tour.currentIndex).toBe(23);
    expect(seen[0]).toBe(0);
    expect(seen).toContain(24);
    tour.stop();
    nav.dispose();
  });

  it("invalid tours stay text-safe: overlay tour with no valid steps never advances", () => {
    const nav = createOpenConnectNavigator({ baseUrl: BASE_URL });
    const tour = nav.runOverlayTour([], { onStep: () => {} });
    expect(tour.textOnly).toBe(true);
    tour.next();
    tour.back();
    tour.goTo(3);
    expect(tour.currentIndex).toBe(0);
    tour.stop();
    nav.dispose();
  });
});

describe("NAV-AT-010 capture denied/redacted by default", () => {
  it("capture defaults off: snapshot stays empty and receipts carry no content", async () => {
    const nav = createOpenConnectNavigator({
      baseUrl: BASE_URL,
      fetchImpl: vi.fn(async () => new Response("forbidden", { status: 403 })),
    });
    expect(nav.captureEnabled).toBe(false);
    await nav.start();

    const snapshot = buildVisibleSnapshot({
      routeId: "/home",
      captureEnabled: nav.captureEnabled,
      nodes: [{ role: "text", label: "Secret", text: "s3cret-password-value" }],
    });
    expect(snapshot.captured).toBe(false);
    expect(snapshot.reason).toBe("capture-disabled");
    expect(snapshot.nodes).toEqual([]);
    expect(JSON.stringify(snapshot)).not.toContain("s3cret");

    for (const intent of ["highlight", "locate"] as const) {
      const receipt = await nav.dispatch({ intent });
      expect(receipt.outcome).toBe("completed");
      expect(JSON.stringify(receipt)).not.toContain("s3cret-password-value");
      expect(JSON.stringify(receipt)).not.toContain("password");
    }
    nav.dispose();
  });

  it("capture-enabled snapshots still redact inputs and sensitive fields", () => {
    const snapshot = buildVisibleSnapshot({
      routeId: "/home",
      captureEnabled: true,
      nodes: [
        { role: "text", label: "Welcome", text: "Welcome" },
        { role: "field", label: "Name", isInput: true, inputName: "name", value: "Ada" },
        { role: "text", label: "Password", text: "s3cret", inputName: "password" },
        { role: "text", label: "Token", text: "tok-abc-123", inputName: "apiKey" },
        { role: "text", label: "Card", text: "4111 1111", inputName: "cardNumber" },
      ],
    });
    expect(snapshot.captured).toBe(true);
    expect(snapshot.nodes[0]?.text).toBe("Welcome");
    for (const node of snapshot.nodes.slice(1)) {
      expect(node.text).toBe(REDACTED_PLACEHOLDER);
      expect(node.redacted).toBe(true);
    }
    const serialized = JSON.stringify(snapshot);
    expect(serialized).not.toContain("Ada");
    expect(serialized).not.toContain("s3cret");
    expect(serialized).not.toContain("tok-abc-123");
    expect(serialized).not.toContain("4111");
  });

  it("navigation without a server grant is denied (fail closed)", async () => {
    const nav = createOpenConnectNavigator({
      baseUrl: BASE_URL,
      fetchImpl: vi.fn(async () => new Response("forbidden", { status: 403 })),
      routeId: "/editor",
    });
    const started = await nav.start();
    expect(started.serverGranted).toBe(false);
    nav.registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    const denied = await nav.dispatch({ intent: "navigate", targetId: "save" });
    expect(denied.outcome).toBe("denied");
    expect(denied.detail.length).toBeGreaterThan(0);
    nav.dispose();
  });
});
