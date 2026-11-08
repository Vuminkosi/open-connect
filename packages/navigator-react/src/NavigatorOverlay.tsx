/**
 * Guided-tour overlay card: step copy plus Next / Back / Skip / Pause controls.
 *
 * SSR-safe: importing this module touches no DOM. `document` / `window` are
 * reached only inside effects guarded by `typeof` checks. All styling is
 * inline (no global CSS); `oc-nav-*` classes are stable test/theme hooks.
 */
import {
  createElement,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from "react";
import type { NavigatorState, NavigatorStep, OpenConnectNavigator } from "./types.js";

export type NavigatorTheme = "light" | "dark" | "system";

export interface NavigatorOverlayProps {
  navigator: OpenConnectNavigator;
  state: NavigatorState;
  step: NavigatorStep;
  announcement: string;
  /**
   * Explicit host theme. `"light"` / `"dark"` always win; `"system"`
   * (default) follows `prefers-color-scheme`. ATLAS tokens preserved.
   */
  theme?: NavigatorTheme;
}

/** SSR-safe `prefers-reduced-motion` probe. */
export function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);
  useEffect(() => {
    if (typeof window !== "undefined" && typeof window.matchMedia === "function") {
      const query = window.matchMedia("(prefers-reduced-motion: reduce)");
      setReduced(query.matches);
      const onChange = (event: MediaQueryListEvent): void => {
        setReduced(event.matches);
      };
      if (typeof query.addEventListener === "function") {
        query.addEventListener("change", onChange);
        return () => {
          query.removeEventListener("change", onChange);
        };
      }
    }
    return () => {};
  }, []);
  return reduced;
}

/** SSR-safe compact-layout probe (600px ATLAS breakpoint). */
export function useCompactLayout(): boolean {
  const [compact, setCompact] = useState(false);
  useEffect(() => {
    if (typeof window !== "undefined" && typeof window.matchMedia === "function") {
      const query = window.matchMedia("(max-width: 599px)");
      setCompact(query.matches);
      const onChange = (event: MediaQueryListEvent): void => {
        setCompact(event.matches);
      };
      if (typeof query.addEventListener === "function") {
        query.addEventListener("change", onChange);
        return () => {
          query.removeEventListener("change", onChange);
        };
      }
    }
    return () => {};
  }, []);
  return compact;
}

/** SSR-safe dark-mode probe so both ATLAS themes work without stylesheets. */
export function useDarkMode(): boolean {
  const [dark, setDark] = useState(false);
  useEffect(() => {
    if (typeof window !== "undefined" && typeof window.matchMedia === "function") {
      const query = window.matchMedia("(prefers-color-scheme: dark)");
      setDark(query.matches);
      const onChange = (event: MediaQueryListEvent): void => {
        setDark(event.matches);
      };
      if (typeof query.addEventListener === "function") {
        query.addEventListener("change", onChange);
        return () => {
          query.removeEventListener("change", onChange);
        };
      }
    }
    return () => {};
  }, []);
  return dark;
}

function controlButton(
  label: string,
  onClick: () => void,
  kind: "primary" | "secondary" | "ghost",
  dark: boolean,
  extra?: { disabled?: boolean; ariaLabel?: string },
): ReactNode {
  const base: CSSProperties = {
    minHeight: 44,
    padding: "10px 12px",
    borderRadius: 8,
    fontSize: 13,
    fontWeight: 600,
    lineHeight: "18px",
    cursor: extra?.disabled === true ? "not-allowed" : "pointer",
    opacity: extra?.disabled === true ? 0.5 : 1,
    border: "1px solid transparent",
  };
  const palette: CSSProperties =
    kind === "primary"
      ? { backgroundColor: dark ? "#6366F1" : "#4F46E5", color: "#FFFFFF" }
      : kind === "secondary"
        ? {
            backgroundColor: "transparent",
            color: dark ? "#E2E8F0" : "#334155",
            border: `1px solid ${dark ? "#334155" : "#DCE2EA"}`,
          }
        : { backgroundColor: "transparent", color: dark ? "#94A3B8" : "#64748B" };
  const props: Record<string, unknown> = {
    type: "button",
    className: `oc-nav-control oc-nav-control-${kind}`,
    onClick,
    style: { ...base, ...palette },
  };
  if (extra?.disabled === true) props["disabled"] = true;
  if (extra?.ariaLabel !== undefined) props["aria-label"] = extra.ariaLabel;
  return createElement("button", props, label);
}

export function NavigatorOverlay(props: NavigatorOverlayProps): ReactNode {
  const { navigator, state, step, announcement, theme = "system" } = props;
  const reducedMotion = useReducedMotion();
  const compact = useCompactLayout();
  const systemDark = useDarkMode();
  const dark = theme === "dark" ? true : theme === "light" ? false : systemDark;
  const panelRef = useRef<HTMLElement | null>(null);
  const restoreFocusRef = useRef<Element | null>(null);

  const paused = state.status === "paused";
  const isFirst = state.index <= 0;
  const isLast = state.index >= state.steps.length - 1;

  // Escape pauses the tour (dismisses the card without losing progress).
  useEffect(() => {
    if (typeof document === "undefined") return () => {};
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        event.preventDefault();
        try {
          navigator.pause();
        } catch {
          // Control callbacks must never throw out of a keyboard handler.
        }
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [navigator]);

  // Focus safety: remember the previously focused element, move focus into
  // the card when a new step arrives, restore focus when the tour overlays
  // unmount. Guards keep this a no-op during server rendering.
  useEffect(() => {
    if (typeof document === "undefined") return () => {};
    if (restoreFocusRef.current === null) {
      restoreFocusRef.current = document.activeElement;
    }
    panelRef.current?.focus();
    return () => {
      const target = restoreFocusRef.current;
      restoreFocusRef.current = null;
      if (target !== null && typeof (target as HTMLElement).focus === "function") {
        try {
          (target as HTMLElement).focus();
        } catch {
          // Best effort: host DOM may already be gone.
        }
      }
    };
    // Re-run per step so screen-reader users land on fresh copy.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [step.id]);

  const call = (fn: () => void): (() => void) => {
    return () => {
      try {
        fn();
      } catch {
        // Overlay controls must never throw into React handlers.
      }
    };
  };

  const surface: CSSProperties = {
    backgroundColor: dark ? "#171F2B" : "#FFFFFF",
    color: dark ? "#F8FAFC" : "#111827",
    border: `1px solid ${dark ? "#334155" : "#DCE2EA"}`,
    borderRadius: 12,
    boxShadow: dark
      ? "0 12px 32px rgba(0, 0, 0, 0.45)"
      : "0 12px 32px rgba(15, 23, 42, 0.18)",
  };

  const card: CSSProperties = compact
    ? {
        ...surface,
        position: "fixed",
        left: 16,
        right: 16,
        bottom: 16,
        zIndex: 1000,
        padding: 16,
        maxHeight: "46vh",
        overflowY: "auto",
      }
    : {
        ...surface,
        position: "fixed",
        right: 16,
        bottom: 16,
        zIndex: 1000,
        width: "min(22rem, calc(100vw - 2rem))",
        padding: 16,
        maxHeight: "70vh",
        overflowY: "auto",
      };

  const positionLabel =
    state.steps.length > 0
      ? `Step ${String(state.index + 1)} of ${String(state.steps.length)}`
      : "Guided tour";

  return createElement(
    "section",
    {
      ref: panelRef,
      className: "oc-nav-overlay",
      role: "dialog",
      "aria-modal": "false",
      "aria-label": `Guided tour: ${step.title}`,
      tabIndex: -1,
      style: card,
    },
    createElement(
      "p",
      {
        className: "oc-nav-position",
        style: { fontSize: 12, lineHeight: "16px", fontWeight: 550, color: dark ? "#94A3B8" : "#64748B", margin: "0 0 4px" },
      },
      paused ? `${positionLabel} · Paused` : positionLabel,
    ),
    createElement(
      "h2",
      {
        className: "oc-nav-title",
        style: { fontSize: 18, lineHeight: "26px", fontWeight: 600, margin: "0 0 4px" },
      },
      step.title,
    ),
    createElement(
      "p",
      {
        className: "oc-nav-body",
        style: { fontSize: 14, lineHeight: "21px", fontWeight: 400, color: dark ? "#CBD5E1" : "#475569", margin: "0 0 12px" },
      },
      step.body,
    ),
    createElement(
      "div",
      {
        className: "oc-nav-controls",
        style: { display: "flex", flexWrap: "wrap", gap: 8, alignItems: "center" },
      },
      controlButton("Back", call(() => navigator.back()), "secondary", dark, {
        disabled: isFirst,
        ariaLabel: "Go back one step",
      }),
      controlButton(isLast ? "Finish" : "Next", call(() => navigator.next()), "primary", dark, {
        ariaLabel: isLast ? "Finish the tour" : "Go to the next step",
      }),
      controlButton(paused ? "Resume" : "Pause", call(() => (paused ? navigator.resume() : navigator.pause())), "secondary", dark, {
        ariaLabel: paused ? "Resume the tour" : "Pause the tour",
      }),
      controlButton("Skip tour", call(() => navigator.skip()), "ghost", dark, {
        ariaLabel: "Skip the rest of the tour",
      }),
    ),
    // Text-only screen-reader channel (no speech synthesis dependency).
    createElement(
      "span",
      {
        className: "oc-nav-announcer",
        role: "status",
        "aria-live": "polite",
        style: visuallyHidden,
      },
      announcement,
    ),
  );
}

const visuallyHidden: CSSProperties = {
  position: "absolute",
  width: 1,
  height: 1,
  padding: 0,
  margin: -1,
  overflow: "hidden",
  clip: "rect(0 0 0 0)",
  whiteSpace: "nowrap",
  borderWidth: 0,
};
