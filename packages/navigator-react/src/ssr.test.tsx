// @vitest-environment node
/**
 * SSR safety (NAV-AT-007): importing the package and server-rendering the
 * provider must never touch `document` / `window` / `matchMedia`.
 */
import { describe, expect, it, vi } from "vitest";
import { renderToString } from "react-dom/server";
import { OpenConnectNavigatorProvider } from "./index.js";
import type { NavigatorState, OpenConnectNavigator } from "./index.js";

describe("SSR safety", () => {
  it("renders on the server with no DOM globals and no DOM side effects", () => {
    expect(typeof (globalThis as { document?: unknown }).document).toBe("undefined");
    expect(typeof (globalThis as { window?: unknown }).window).toBe("undefined");

    const state: NavigatorState = {
      status: "active",
      index: 0,
      steps: [{ id: "s-1", target: "#save", title: "Save your work", body: "Press Save." }],
    };
    const navigator: OpenConnectNavigator = {
      getState: () => state,
      subscribe: () => () => undefined,
      next: vi.fn(),
      back: vi.fn(),
      skip: vi.fn(),
      pause: vi.fn(),
      resume: vi.fn(),
      dispose: vi.fn(),
    };

    let html = "";
    expect(() => {
      html = renderToString(
        <OpenConnectNavigatorProvider navigator={navigator}>
          <p>Application content</p>
        </OpenConnectNavigatorProvider>,
      );
    }).not.toThrow();

    expect(html).toContain("Application content");
    expect(html).toContain("Save your work");
    // Highlighter is client-only: no positioned box in server markup.
    expect(html).not.toContain("oc-nav-highlight");
    // Effects never ran server-side, so no subscription happened.
    expect(html).toContain("oc-nav-announcer");
  });
});
