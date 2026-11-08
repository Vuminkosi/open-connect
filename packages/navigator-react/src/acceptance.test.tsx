/**
 * NAV-CLOSE-03 React acceptance evidence.
 * Maps the provider/overlay to NAV-AT-002 (static text tour channel),
 * NAV-AT-007 (MiniComposer coexistence, navigator never mutated), and
 * NAV-AT-010 (capture denied/redacted by default: host secrets never leak
 * into the tour DOM).
 */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OpenConnectNavigatorProvider } from "./NavigatorProvider.js";
import type { NavigatorState, NavigatorStep, OpenConnectNavigator } from "./types.js";

const steps: NavigatorStep[] = [
  { id: "s-1", target: "", title: "Save your work", body: "Press Save to keep changes." },
  { id: "s-2", target: "", title: "All done", body: "You finished the tour." },
];

function createFakeNavigator(): OpenConnectNavigator {
  let state: NavigatorState = { status: "active", index: 0, steps };
  const listeners = new Set<() => void>();
  return {
    getState: () => state,
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    next: () => {
      state =
        state.index < steps.length - 1
          ? { ...state, index: state.index + 1 }
          : { ...state, status: "done" };
      for (const listener of [...listeners]) listener();
    },
    back: () => {
      state = { ...state, index: Math.max(0, state.index - 1) };
      for (const listener of [...listeners]) listener();
    },
    skip: () => {
      state = { ...state, status: "skipped" };
      for (const listener of [...listeners]) listener();
    },
    pause: () => {
      state = { ...state, status: "paused" };
      for (const listener of [...listeners]) listener();
    },
    resume: () => {
      state = { ...state, status: "active" };
      for (const listener of [...listeners]) listener();
    },
    dispose: () => undefined,
  };
}

/** Stub with the same regions as the real MiniComposer (FAB + bubble). */
function StubMiniComposer(): React.ReactNode {
  return (
    <div className="oc-composer-host">
      <button type="button" className="oc-composer-fab" aria-label="Open Assistant">
        {"\u2726"}
      </button>
      <section className="oc-composer-bubble" role="dialog" aria-label="Assistant composer">
        <p className="oc-composer-empty">Ask a question to start.</p>
      </section>
    </div>
  );
}

let root: Root | null = null;
let host: HTMLDivElement | null = null;

function mount(children: React.ReactNode): HTMLElement {
  host = document.createElement("div");
  document.body.appendChild(host);
  root = createRoot(host);
  act(() => {
    root?.render(children);
  });
  if (host === null) throw new Error("mount failed");
  return host;
}

afterEach(() => {
  act(() => {
    root?.unmount();
  });
  root = null;
  host?.remove();
  host = null;
});

describe("NAV-AT-002 static text tour channel (React)", () => {
  it("renders step title/body text plus a Step X of N announcement beside app content", () => {
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );
    // Static text channel: title + body are plain readable text.
    expect(container.textContent).toContain("Application content");
    expect(container.textContent).toContain("Save your work");
    expect(container.textContent).toContain("Press Save to keep changes.");
    const announcer = container.querySelector(".oc-nav-announcer");
    expect(announcer?.getAttribute("aria-live")).toBe("polite");
    expect(announcer?.textContent).toContain("Step 1 of 2");
  });

  it("advancing the tour updates the text channel to the next step", () => {
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );
    act(() => {
      navigator.next();
    });
    expect(container.textContent).toContain("All done");
    expect(container.textContent).toContain("You finished the tour.");
    expect(container.querySelector(".oc-nav-announcer")?.textContent).toContain("Step 2 of 2");
  });
});

describe("NAV-AT-007 provider coexistence (React)", () => {
  it("composes the stub MiniComposer without replacing it; overlay is a sibling", () => {
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <StubMiniComposer />
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );
    expect(container.querySelector(".oc-composer-fab")).not.toBeNull();
    expect(container.querySelector(".oc-composer-bubble")).not.toBeNull();
    expect(container.textContent).toContain("Ask a question to start.");
    expect(container.querySelector(".oc-nav-overlay")).not.toBeNull();
    // Overlay is a sibling of the composer host, not a parent/child takeover.
    const composerHost = container.querySelector(".oc-composer-host");
    const overlay = container.querySelector(".oc-nav-overlay");
    expect(composerHost).not.toBeNull();
    expect(overlay).not.toBeNull();
    expect(overlay?.closest(".oc-composer-host")).toBeNull();
  });

  it("never mutates the navigator handle object identity", () => {
    const navigator = createFakeNavigator();
    const keysBefore = Object.keys(navigator).sort();
    const getStateBefore = navigator.getState;
    mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );
    expect(Object.keys(navigator).sort()).toEqual(keysBefore);
    expect(navigator.getState).toBe(getStateBefore);
  });

  it("renders children only (pass-through) when no navigator is attached", () => {
    const container = mount(
      <OpenConnectNavigatorProvider>
        <StubMiniComposer />
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );
    expect(container.textContent).toContain("Application content");
    expect(container.querySelector(".oc-composer-fab")).not.toBeNull();
    expect(container.querySelector(".oc-nav-overlay")).toBeNull();
  });
});

describe("NAV-AT-010 capture denied/redacted by default (React)", () => {
  it("never leaks host input values into the tour overlay or announcer", () => {
    const secret = "s3cret-host-password-value";
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <div>
          <label>
            Password
            <input type="password" name="password" defaultValue={secret} />
          </label>
          <p>Application content</p>
        </div>
      </OpenConnectNavigatorProvider>,
    );
    const overlay = container.querySelector(".oc-nav-overlay");
    expect(overlay).not.toBeNull();
    expect(overlay?.textContent).not.toContain(secret);
    expect(container.querySelector(".oc-nav-announcer")?.textContent).not.toContain(secret);
    // The tour DOM carries no snapshot/capture nodes — text channel only.
    expect(container.querySelector("[data-capture]")).toBeNull();
    expect(container.querySelector(".oc-nav-snapshot")).toBeNull();
  });

  it("host secrets stay out of the tour DOM after step navigation", () => {
    const secret = "tok-host-secret-abc123";
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <div>
          <input type="text" name="apiKey" defaultValue={secret} aria-label="API key" />
          <p>Application content</p>
        </div>
      </OpenConnectNavigatorProvider>,
    );
    const dispose = vi.spyOn(navigator, "dispose");
    act(() => {
      navigator.next();
    });
    expect(container.textContent).not.toContain(secret);
    expect(container.querySelector(".oc-nav-announcer")?.textContent).not.toContain(secret);
    expect(dispose).not.toHaveBeenCalled();
  });
});
