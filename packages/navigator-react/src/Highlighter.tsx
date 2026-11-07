/**
 * Target highlighter for guided tours.
 *
 * SSR-safe: renders nothing until the client-side effect resolves the
 * selector to a live element. Missing targets never throw — the overlay card
 * still carries the step copy so the tour stays usable.
 *
 * Styling is inline only (no global CSS); `oc-nav-*` class names are stable
 * hooks for host apps that want to layer their own theme on top.
 */
import { createElement, useEffect, useState, type CSSProperties } from "react";

export interface HighlighterProps {
  /** CSS selector of the element to outline; empty disables highlighting. */
  target: string;
  /** True when the user prefers reduced motion (disables transitions). */
  reducedMotion: boolean;
  /** True while the overlay is in dark mode. */
  dark: boolean;
}

interface Box {
  top: number;
  left: number;
  width: number;
  height: number;
}

function resolveBox(selector: string): Box | null {
  if (typeof document === "undefined" || selector.trim().length === 0) return null;
  let element: Element | null = null;
  try {
    element = document.querySelector(selector);
  } catch {
    return null;
  }
  if (element === null) return null;
  if (typeof element.getBoundingClientRect !== "function") return null;
  const rect = element.getBoundingClientRect();
  return { top: rect.top, left: rect.left, width: rect.width, height: rect.height };
}

const PADDING = 6;

export function Highlighter(props: HighlighterProps): React.ReactNode {
  const [box, setBox] = useState<Box | null>(null);

  useEffect(() => {
    setBox(resolveBox(props.target));
    if (typeof window === "undefined") return;
    const onChange = (): void => {
      setBox(resolveBox(props.target));
    };
    window.addEventListener("resize", onChange);
    window.addEventListener("scroll", onChange, true);
    return () => {
      window.removeEventListener("resize", onChange);
      window.removeEventListener("scroll", onChange, true);
    };
  }, [props.target]);

  if (box === null) return null;

  const style: CSSProperties = {
    position: "fixed",
    top: Math.max(0, box.top - PADDING),
    left: Math.max(0, box.left - PADDING),
    width: Math.max(0, box.width + PADDING * 2),
    height: Math.max(0, box.height + PADDING * 2),
    border: `2px solid ${props.dark ? "#818CF8" : "#4F46E5"}`,
    borderRadius: 10,
    boxShadow: props.dark
      ? "0 0 0 4px rgba(129, 140, 248, 0.25)"
      : "0 0 0 4px rgba(79, 70, 229, 0.18)",
    pointerEvents: "none",
    zIndex: 1000,
    transition: props.reducedMotion ? "none" : "top 160ms ease, left 160ms ease, width 160ms ease, height 160ms ease",
  };

  return createElement("div", {
    className: "oc-nav-highlight",
    "aria-hidden": "true",
    style,
  });
}
