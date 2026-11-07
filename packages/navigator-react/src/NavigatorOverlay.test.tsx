/**
 * NAV-REM-03: overlay remediation evidence.
 * - NAV-UX-01 (S2): controls >= 44px compact height.
 * - NAV-UX-04 (S2): explicit host `theme` prop wins over system.
 * - NAV-UX-05 (S3): focus order/restoration + Escape DOM-tested.
 */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { NavigatorOverlay } from "./NavigatorOverlay.js";
import type { NavigatorState, NavigatorStep, OpenConnectNavigator } from "./types.js";

const step: NavigatorStep = {
  id: "s-1",
  target: "",
  title: "Save your work",
  body: "Press Save to keep changes.",
};

const state: NavigatorState = { status: "active", index: 0, steps: [step] };

function createStubNavigator(): OpenConnectNavigator & {
  pause: ReturnType<typeof vi.fn>;
} {
  const pause = vi.fn((): void => undefined);
  return {
    getState: () => state,
    subscribe: () => () => undefined,
    next: () => undefined,
    back: () => undefined,
    skip: () => undefined,
    pause,
    resume: () => undefined,
    dispose: () => undefined,
  };
}

let root: Root | null = null;
let host: HTMLDivElement | null = null;

function mountOverlay(props?: {
  theme?: "light" | "dark" | "system";
  announcement?: string;
}): HTMLElement {
  const navigator = createStubNavigator();
  host = document.createElement("div");
  document.body.appendChild(host);
  root = createRoot(host);
  act(() => {
    const themeProps = props?.theme === undefined ? {} : { theme: props.theme };
    root?.render(
      <NavigatorOverlay
        navigator={navigator}
        state={state}
        step={step}
        announcement={props?.announcement ?? "Step 1 of 1. Save your work."}
        {...themeProps}
      />,
    );
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

/** Force a deterministic `prefers-color-scheme` answer for system-theme tests. */
function mockColorScheme(systemDark: boolean): void {
  const fn = (query: string): MediaQueryList => {
    const matches = query.includes("prefers-color-scheme: dark")
      ? systemDark
      : query.includes("max-width")
        ? false
        : false;
    return {
      matches,
      media: query,
      onchange: null,
      addListener: () => undefined,
      removeListener: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      dispatchEvent: () => false,
    } as unknown as MediaQueryList;
  };
  Object.defineProperty(window, "matchMedia", { value: fn, writable: true, configurable: true });
}

describe("NAV-REM-03 overlay remediation", () => {
  it("NAV-UX-01: every control is at least 44px tall (compact touch target)", () => {
    const container = mountOverlay();
    const controls = [...container.querySelectorAll<HTMLButtonElement>(".oc-nav-control")];
    expect(controls.length).toBe(4);
    for (const button of controls) {
      // Inline-style class contract (no global CSS in this package).
      expect(button.style.minHeight).toBe("44px");
      const computed = getComputedStyle(button).minHeight;
      const px = Number.parseFloat(computed);
      // happy-dom may report "" when layout is unavailable; the inline
      // contract above is authoritative, computed is best-effort.
      if (computed !== "") expect(px).toBeGreaterThanOrEqual(44);
    }
  });

  it("NAV-UX-04: explicit theme='dark' wins even when the system is light", () => {
    mockColorScheme(false);
    const container = mountOverlay({ theme: "dark" });
    const dialog = container.querySelector<HTMLElement>(".oc-nav-overlay");
    expect(dialog).not.toBeNull();
    // ATLAS dark surface #171F2B (happy-dom keeps the authored hex form).
    expect(dialog?.style.backgroundColor.toLowerCase()).toMatch(/#171f2b|rgb\(23,\s*31,\s*43\)/);
  });

  it("NAV-UX-04: explicit theme='light' wins even when the system is dark", () => {
    mockColorScheme(true);
    const container = mountOverlay({ theme: "light" });
    const dialog = container.querySelector<HTMLElement>(".oc-nav-overlay");
    expect(dialog).not.toBeNull();
    // ATLAS light surface #FFFFFF.
    expect(dialog?.style.backgroundColor.toLowerCase()).toMatch(/#ffffff|rgb\(255,\s*255,\s*255\)/);
  });

  it("NAV-UX-04: omitted theme follows the system (dark system -> dark surface)", () => {
    mockColorScheme(true);
    const container = mountOverlay();
    const dialog = container.querySelector<HTMLElement>(".oc-nav-overlay");
    expect(dialog?.style.backgroundColor.toLowerCase()).toMatch(/#171f2b|rgb\(23,\s*31,\s*43\)/);
  });

  it("NAV-UX-05: focus moves into the dialog on step mount", () => {
    const container = mountOverlay();
    const dialog = container.querySelector<HTMLElement>(".oc-nav-overlay");
    expect(document.activeElement).toBe(dialog);
  });

  it("NAV-UX-05: unmount restores focus to the previously focused element", () => {
    const trigger = document.createElement("button");
    trigger.textContent = "Start tour";
    document.body.appendChild(trigger);
    trigger.focus();
    expect(document.activeElement).toBe(trigger);

    mountOverlay();
    const dialog = document.querySelector<HTMLElement>(".oc-nav-overlay");
    expect(document.activeElement).toBe(dialog);

    act(() => {
      root?.unmount();
    });
    root = null;
    expect(document.activeElement).toBe(trigger);
    trigger.remove();
  });

  it("NAV-UX-05: Escape pauses the tour; other keys do not", () => {
    const navigator = createStubNavigator();
    host = document.createElement("div");
    document.body.appendChild(host);
    root = createRoot(host);
    act(() => {
      root?.render(
        <NavigatorOverlay navigator={navigator} state={state} step={step} announcement="Step 1" />,
      );
    });

    act(() => {
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    });
    expect(navigator.pause).not.toHaveBeenCalled();

    act(() => {
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    });
    expect(navigator.pause).toHaveBeenCalledTimes(1);
  });
});
