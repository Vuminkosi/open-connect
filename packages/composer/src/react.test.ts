// @vitest-environment node
/**
 * SSR safety & React wrapper verification (OC2-D-CLIENT-018, CMP-AT-017, CMP-AT-024).
 * Ensures:
 * 1. Importing @openconnect/composer/react touches no DOM globals (`window`, `document`).
 * 2. `OpenConnectProvider` and `MiniComposer` render cleanly via react-dom/server without DOM.
 * 3. Initial element tree structure matches the framework-neutral specification.
 */
import { describe, expect, it, vi } from "vitest";
import { renderToString } from "react-dom/server";
import { createElement } from "react";
import { OpenConnectProvider, MiniComposer } from "./react.js";
import type { ComposerClient } from "./composer.js";

describe("React adapter SSR & provider integration (OC2-D-CLIENT-018)", () => {
  it("renders on the server without DOM globals or side effects", () => {
    expect(typeof (globalThis as { document?: unknown }).document).toBe("undefined");
    expect(typeof (globalThis as { window?: unknown }).window).toBe("undefined");

    const client: ComposerClient = {
      createTask: vi.fn(),
      subscribeEvents: vi.fn(() => () => {}),
    };

    let html = "";
    expect(() => {
      html = renderToString(
        createElement(
          OpenConnectProvider,
          { client, sessionId: "sess-1", agentId: "assistant" },
          createElement(MiniComposer, { assistantName: "TestAssistant" }),
        ),
      );
    }).not.toThrow();

    expect(html).toContain("TestAssistant");
    expect(html).toContain("oc-composer-host");
    expect(html).toContain("oc-composer-fab");
  });

  it("shares underlying client without duplicate task creation on render", () => {
    const client: ComposerClient = {
      createTask: vi.fn(),
      subscribeEvents: vi.fn(() => () => {}),
    };

    renderToString(
      createElement(
        OpenConnectProvider,
        { client, sessionId: "sess-42" },
        createElement(MiniComposer, null),
      ),
    );

    // SSR render does not invoke network or task mutations
    expect(client.createTask).not.toHaveBeenCalled();
    // Subscriptions only attach in browser useEffect
    expect(client.subscribeEvents).not.toHaveBeenCalled();
  });
});
