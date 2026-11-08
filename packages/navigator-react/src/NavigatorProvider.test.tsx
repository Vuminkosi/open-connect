/**
 * NAV-AT-007: host MiniComposer + Navigator provider coexist; controls and
 * Escape invoke navigator callbacks; unmount disposes.
 *
 * Runs in happy-dom (real React DOM effects) with a stub MiniComposer that
 * mimics the `@openconnect/composer/react` regions — the navigator provider
 * must compose it, never replace it.
 */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OpenConnectNavigatorProvider } from "./NavigatorProvider.js";
import type { NavigatorState, NavigatorStep, OpenConnectNavigator } from "./types.js";

const steps: NavigatorStep[] = [
  { id: "s-1", target: "#stub-target", title: "Save your work", body: "Press Save to keep changes." },
  { id: "s-2", target: "", title: "All done", body: "You finished the tour." },
];

interface FakeNavigator extends OpenConnectNavigator {
  emit(): void;
}

function createFakeNavigator(): FakeNavigator & {
  calls: Record<"next" | "back" | "skip" | "pause" | "resume" | "dispose", ReturnType<typeof vi.fn>>;
} {
  let state: NavigatorState = { status: "active", index: 0, steps };
  const listeners = new Set<() => void>();
  const emit = (): void => {
    for (const listener of [...listeners]) listener();
  };
  const next = vi.fn((): void => {
    if (state.index < steps.length - 1) state = { ...state, index: state.index + 1 };
    else state = { ...state, status: "done" };
    emit();
  });
  const back = vi.fn((): void => {
    if (state.index > 0) state = { ...state, index: state.index - 1 };
    emit();
  });
  const skip = vi.fn((): void => {
    state = { ...state, status: "skipped" };
    emit();
  });
  const pause = vi.fn((): void => {
    state = { ...state, status: "paused" };
    emit();
  });
  const resume = vi.fn((): void => {
    state = { ...state, status: "active" };
    emit();
  });
  const dispose = vi.fn((): void => undefined);
  return {
    getState: () => state,
    subscribe: (listener: () => void): (() => void) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    next,
    back,
    skip,
    pause,
    resume,
    dispose,
    emit,
    calls: { next, back, skip, pause, resume, dispose },
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

function clickButton(container: HTMLElement, label: string): void {
  const buttons = [...container.querySelectorAll("button")];
  const target = buttons.find((button) => button.textContent === label);
  if (target === undefined) throw new Error(`button "${label}" not found`);
  act(() => {
    target.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  });
}

describe("NAV-AT-007 coexistence + controls", () => {
  it("renders children and the stub MiniComposer side by side with the tour overlay", () => {
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <StubMiniComposer />
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );

    // Host app and composer are untouched.
    expect(container.textContent).toContain("Application content");
    expect(container.querySelector(".oc-composer-fab")).not.toBeNull();
    expect(container.querySelector(".oc-composer-bubble")).not.toBeNull();
    // Tour overlay is a sibling, not a replacement.
    expect(container.querySelector(".oc-nav-overlay")).not.toBeNull();
    expect(container.textContent).toContain("Save your work");
    const announcer = container.querySelector(".oc-nav-announcer");
    expect(announcer?.getAttribute("aria-live")).toBe("polite");
    expect(announcer?.textContent).toContain("Step 1 of 2");
  });

  it("Next / Back / Skip / Pause controls invoke navigator callbacks", () => {
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );

    clickButton(container, "Next");
    expect(navigator.calls.next).toHaveBeenCalledTimes(1);
    expect(container.textContent).toContain("All done");

    clickButton(container, "Back");
    expect(navigator.calls.back).toHaveBeenCalledTimes(1);
    expect(container.textContent).toContain("Save your work");

    clickButton(container, "Pause");
    expect(navigator.calls.pause).toHaveBeenCalledTimes(1);
    expect(container.textContent).toContain("Paused");

    clickButton(container, "Resume");
    expect(navigator.calls.resume).toHaveBeenCalledTimes(1);

    clickButton(container, "Skip tour");
    expect(navigator.calls.skip).toHaveBeenCalledTimes(1);
    // Skipped tours remove the overlay but keep the host content.
    expect(container.querySelector(".oc-nav-overlay")).toBeNull();
    expect(container.textContent).toContain("Application content");
  });

  it("Escape pauses the tour", () => {
    const navigator = createFakeNavigator();
    mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );

    act(() => {
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    });
    expect(navigator.calls.pause).toHaveBeenCalledTimes(1);
  });

  it("unmount unsubscribes and disposes the navigator exactly once", () => {
    const navigator = createFakeNavigator();
    const container = mount(
      <OpenConnectNavigatorProvider navigator={navigator}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );
    expect(container.querySelector(".oc-nav-overlay")).not.toBeNull();

    act(() => {
      root?.unmount();
    });
    root = null;
    expect(navigator.calls.dispose).toHaveBeenCalledTimes(1);
    // Listener removed: post-unmount emits must not touch React state.
    expect(() => navigator.emit()).not.toThrow();
  });

  it("disposeOnUnmount=false keeps an externally owned navigator alive", () => {
    const navigator = createFakeNavigator();
    mount(
      <OpenConnectNavigatorProvider navigator={navigator} disposeOnUnmount={false}>
        <p>Application content</p>
      </OpenConnectNavigatorProvider>,
    );

    act(() => {
      root?.unmount();
    });
    root = null;
    expect(navigator.calls.dispose).not.toHaveBeenCalled();
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
