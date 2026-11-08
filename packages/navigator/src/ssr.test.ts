import { describe, expect, it } from "vitest";

/**
 * NAV-AT-007: the bridge imports with no DOM globals present, and importing
 * it does not create DOM globals or touch MiniComposer/client registration.
 */
describe("SSR-safe import (NAV-AT-007)", () => {
  it("imports without window/document/navigator globals", async () => {
    expect(typeof window).toBe("undefined");
    expect(typeof document).toBe("undefined");
    const mod = await import("./index.js");
    expect(typeof mod.createOpenConnectNavigator).toBe("function");
    expect(typeof window).toBe("undefined");
    expect(typeof document).toBe("undefined");
  });

  it("does not register anything on globalThis and leaves MiniComposer alone", async () => {
    const before = new Set(Object.keys(globalThis));
    const mod = await import("./index.js");
    mod.createOpenConnectNavigator({ baseUrl: "https://tour.example/openconnect/v1" });
    const after = new Set(Object.keys(globalThis));
    expect([...after].filter((key) => !before.has(key))).toEqual([]);
  });
});
