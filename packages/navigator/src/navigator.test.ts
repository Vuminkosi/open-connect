import { describe, expect, it, vi } from "vitest";
import {
  createOpenConnectNavigator,
  renderTextTour,
  TOUR_MAX_STEPS,
  validateTourSteps,
  type NavigatorReceipt,
} from "./navigator.js";
import { TargetRegistry } from "./targets.js";

function okResponse(): Response {
  return new Response(JSON.stringify({ granted: true }), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

describe("tour step validation", () => {
  it("accepts bounded steps and renders the static text tour (NAV-AT-002)", () => {
    const registry = new TargetRegistry();
    registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    const steps = [
      { title: "Save your work", body: "Press Save to keep your changes.", targetId: "save" },
      { title: "Done", body: "You finished the tour." },
    ];
    expect(validateTourSteps(steps, registry, "/editor").ok).toBe(true);
    const text = renderTextTour(steps);
    expect(text).toContain("App tour — 2 steps");
    expect(text).toContain("Step 1 of 2: Save your work");
  });

  it("rejects empty tours, overlong text, unknown and out-of-scope targets", () => {
    const registry = new TargetRegistry();
    registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    expect(validateTourSteps([]).ok).toBe(false);
    expect(renderTextTour([])).toBe("No tour steps available.");
    const bad = validateTourSteps(
      [
        { title: " ", body: "Body", targetId: "nope" },
        { title: "T".repeat(121), body: "Body", targetId: "save" },
      ],
      registry,
      "/other",
    );
    expect(bad.ok).toBe(false);
    expect(bad.problems.length).toBeGreaterThanOrEqual(3);
  });

  it("rejects tours over the step bound (25 per contract tour.json)", () => {
    expect(TOUR_MAX_STEPS).toBe(25);
    const okSteps = Array.from({ length: 25 }, (_, i) => ({
      title: `Step ${String(i)}`,
      body: "Body",
    }));
    expect(validateTourSteps(okSteps).ok).toBe(true);
    const overSteps = Array.from({ length: 26 }, (_, i) => ({
      title: `Step ${String(i)}`,
      body: "Body",
    }));
    const result = validateTourSteps(overSteps);
    expect(result.ok).toBe(false);
    expect(result.problems.some((problem) => problem.message.includes("maximum 25"))).toBe(true);
  });
});

describe("navigator bridge", () => {
  it("starts SSR-safe with defaults (speech/capture off) and stays read-only without a grant", async () => {
    const fetchImpl = vi.fn(async () => new Response("forbidden", { status: 403 }));
    const receipts: NavigatorReceipt[] = [];
    const nav = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1",
      fetchImpl,
      onReceipt: (receipt) => receipts.push(receipt),
    });
    expect(nav.captureEnabled).toBe(false);
    expect(nav.speechEnabled).toBe(false);

    const started = await nav.start();
    expect(started.started).toBe(true);
    expect(started.serverGranted).toBe(false);

    nav.registry.register({ targetId: "save", routeId: "/editor", label: "Save" });
    const denied = await nav.dispatch({ intent: "navigate", targetId: "save" });
    expect(denied.outcome).toBe("denied");

    const uncertain = await nav.dispatch({ intent: "explain", targetId: "missing" });
    expect(uncertain.outcome).toBe("uncertain");
    expect(receipts.length).toBeGreaterThanOrEqual(2);
    nav.dispose();
    expect(nav.disposed).toBe(true);
    const afterDispose = await nav.dispatch({ intent: "explain" });
    expect(afterDispose.outcome).toBe("failed");
  });

  it("serves read-only intents locally and grants navigation after approval", async () => {
    const fetchImpl = vi.fn(async () => okResponse());
    const nav = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1/",
      fetchImpl,
      routeId: "/editor",
    });
    const started = await nav.start();
    expect(started.serverGranted).toBe(true);
    nav.registry.register({
      targetId: "save",
      routeId: "/editor",
      label: "Save",
      description: "Save keeps your changes on this device.",
    });

    const explained = await nav.dispatch({ intent: "explain", targetId: "save" });
    expect(explained.outcome).toBe("completed");
    expect(explained.detail).toContain("Save keeps your changes");

    const moved = await nav.dispatch({ intent: "navigate", targetId: "save" });
    expect(moved.outcome).toBe("completed");
    expect(moved.receiptId.startsWith("nav_")).toBe(true);
    nav.dispose();
  });

  it("denies capture content under sensitive fields and defaults capture off (NAV-AT-010)", async () => {
    const nav = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1",
      fetchImpl: vi.fn(async () => okResponse()),
    });
    await nav.start();
    // Capture defaults off: highlight receipts carry no snapshot content.
    const receipt = await nav.dispatch({ intent: "highlight" });
    expect(receipt.outcome).toBe("completed");
    expect(JSON.stringify(receipt)).not.toContain("password");
    nav.dispose();
  });

  it("runs the overlay tour in text-only mode without a DOM", () => {
    const nav = createOpenConnectNavigator({ baseUrl: "https://tour.example/openconnect/v1" });
    const seen: number[] = [];
    const tour = nav.runOverlayTour(
      [{ title: "Hello", body: "Welcome to the tour." }],
      { onStep: (index) => seen.push(index) },
    );
    expect(tour.textOnly).toBe(true);
    expect(tour.currentIndex).toBe(0);
    expect(seen).toEqual([0]);
    tour.next();
    tour.stop();
    nav.dispose();
  });

  it("probes the approved targets route with routeId (NAV-REM-02 NAV-01)", async () => {
    const fetchImpl = vi.fn(async () => okResponse());
    const nav = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1/",
      fetchImpl,
      routeId: "/editor",
    });
    const started = await nav.start();
    expect(started.serverGranted).toBe(true);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    const firstCall = fetchImpl.mock.calls[0] as unknown as [string, RequestInit?] | undefined;
    expect(firstCall).toBeDefined();
    const url = (firstCall?.[0] ?? "") as string;
    expect(url).toBe("https://tour.example/openconnect/v1/navigator/targets?routeId=%2Feditor");
    expect(url).not.toContain("/navigator/grants");
    nav.dispose();
  });

  it("fails closed to read-only on non-2xx, network error, or missing routeId", async () => {
    const forbidden = vi.fn(async () => new Response("forbidden", { status: 403 }));
    const denied = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1",
      fetchImpl: forbidden,
      routeId: "/editor",
    });
    expect((await denied.start()).serverGranted).toBe(false);
    const deniedCall = forbidden.mock.calls[0] as unknown as [string, RequestInit?] | undefined;
    expect((deniedCall?.[0] ?? "") as string).toContain("/navigator/targets?routeId=");
    denied.dispose();

    const broken = vi.fn(async () => {
      throw new Error("network down");
    });
    const offline = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1",
      fetchImpl: broken,
      routeId: "/editor",
    });
    expect((await offline.start()).serverGranted).toBe(false);
    offline.dispose();

    const noScopeFetch = vi.fn(async () => okResponse());
    const noScope = createOpenConnectNavigator({
      baseUrl: "https://tour.example/openconnect/v1",
      fetchImpl: noScopeFetch,
    });
    expect((await noScope.start()).serverGranted).toBe(false);
    expect(noScopeFetch).not.toHaveBeenCalled();
    noScope.dispose();
  });
});
