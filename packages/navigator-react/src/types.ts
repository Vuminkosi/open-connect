/**
 * Minimal navigator bridge contract (NAV-CLI-02, local).
 *
 * TODO(NAV-CLI-01): reconcile with the `createOpenConnectNavigator` return
 * type from `@openconnect/navigator` once the bridge lands. This interface is
 * intentionally structural — the real bridge satisfies it as long as it
 * exposes `getState` / `subscribe` / step controls / `dispose` — so this
 * package builds and ships with the bridge absent.
 *
 * Importing this module touches no DOM and is SSR-safe.
 */

/** One guided step: human copy plus the selector of the element to highlight. */
export interface NavigatorStep {
  /** Stable step id (used for keys and announcements). */
  id: string;
  /** CSS selector of the element to highlight; empty targets the overlay alone. */
  target: string;
  /** Short headline shown in the overlay card. */
  title: string;
  /** One or two sentences of guidance shown in the overlay card. */
  body: string;
}

export type NavigatorStatus = "idle" | "active" | "paused" | "done" | "skipped";

/** Immutable snapshot published by the navigator. */
export interface NavigatorState {
  status: NavigatorStatus;
  /** Zero-based index of the current step. */
  index: number;
  /** Ordered tour steps. */
  steps: readonly NavigatorStep[];
}

export type NavigatorListener = () => void;

/**
 * Structural handle for a guided tour. Mirrors the `createMiniComposer`
 * handle shape (`getState` / `subscribe` / `dispose`) so both widgets share
 * one lifecycle vocabulary.
 */
export interface OpenConnectNavigator {
  getState(): NavigatorState;
  subscribe(listener: NavigatorListener): () => void;
  next(): void;
  back(): void;
  skip(): void;
  pause(): void;
  resume(): void;
  dispose(): void;
}

/** Step currently shown, or `null` when the tour is not on a step. */
export function currentStep(state: NavigatorState): NavigatorStep | null {
  if (state.status !== "active" && state.status !== "paused") return null;
  return state.steps[state.index] ?? null;
}

/** Screen-reader announcement for the current snapshot (text-only channel). */
export function announceFor(state: NavigatorState): string {
  const step = currentStep(state);
  if (step === null) {
    if (state.status === "done") return "Tour complete.";
    if (state.status === "skipped") return "Tour skipped.";
    return "Tour closed.";
  }
  const position =
    state.steps.length > 0 ? `Step ${String(state.index + 1)} of ${String(state.steps.length)}. ` : "";
  const paused = state.status === "paused" ? "Paused. " : "";
  return `${paused}${position}${step.title}. ${step.body}`;
}
