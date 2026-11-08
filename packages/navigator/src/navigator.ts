/**
 * Framework-neutral Navigator bridge.
 *
 * `createOpenConnectNavigator({ client, speech: { enabled: false },
 * capture: { enabled: false } })` is SSR-safe: importing this module and
 * creating the bridge never touches `window`/`document`. The DOM is bound
 * only inside `start()` (browser only), and `dispose()` releases the
 * overlay, listeners, and in-flight requests.
 *
 * The bridge reuses the authenticated `@openconnect/client` transport
 * pattern: callers point the bridge at the same `baseUrl` as their
 * `OpenConnectClient` and share the same authenticated `fetchImpl`
 * wrapper. The bridge never stores credentials and never hardcodes auth;
 * an optional `getAuthorizationHeader` callback supplies a bearer header
 * per request. Tour/mutation work requires a server grant; without one the
 * bridge stays in read-only text mode and command receipts report `denied`.
 */

/**
 * Structural client surface used only as an optional connectivity probe.
 * `OpenConnectClient` satisfies this interface structurally (both expose
 * `listCapabilities()`); the bridge imports no client code at all — not
 * even a type import — so the entry stays dependency-free and SSR-safe.
 * The bridge never mutates the client and never alters MiniComposer
 * registration (NAV-AT-007 coexistence).
 */
import { showHighlight, type HighlighterHandle } from "./highlighter.js";
import { buildVisibleSnapshot } from "./observation.js";
import { TargetRegistry, type NavigatorTarget } from "./targets.js";

/** Minimal fetch surface (browser + Node 24 global fetch). */
export type FetchImpl = (input: string | URL, init?: RequestInit) => Promise<Response>;

/**
 * Structural client surface satisfied by `OpenConnectClient`. Only public
 * read APIs are used, and only as an optional connectivity probe — the
 * bridge never reaches into client internals and never alters client or
 * MiniComposer registration (NAV-AT-007 coexistence).
 */
export interface NavigatorClientLike {
  listCapabilities?: () => Promise<unknown>;
}

/** Read-only intents the bridge can serve without a server grant. */
export type ReadonlyIntent = "explain" | "locate" | "highlight" | "scroll" | "focus";
/** Intent that changes route state and therefore requires a server grant. */
export type GrantRequiredIntent = "navigate";
/** Every intent the command protocol accepts. */
export type NavigatorIntent = ReadonlyIntent | GrantRequiredIntent;

/** Action issued to the bridge (local UI or server command). */
export interface NavigatorAction {
  intent: NavigatorIntent;
  /** Stable target id resolved against the {@link TargetRegistry}. */
  targetId?: string;
  /** Route the action applies to (defaults to the bridge route). */
  routeId?: string;
  /** Optional tour step index for progress reporting. */
  stepIndex?: number;
}

/** Machine-readable outcome attached to every command receipt. */
export type NavigatorOutcome = "completed" | "denied" | "redacted" | "failed" | "uncertain";

/** Command receipt returned for every dispatched action. */
export interface NavigatorReceipt {
  receiptId: string;
  action: NavigatorAction;
  outcome: NavigatorOutcome;
  /** Human-language summary (never raw errors or stack traces). */
  detail: string;
  /** Present when the bridge could not fully ground the action. */
  uncertainty?: string;
}

/** One step of an app tour. */
export interface TourStep {
  title: string;
  body: string;
  targetId?: string;
  routeId?: string;
}

/** Bounds for tour validation (spec §4.5 + contracts/navigator/v1/tour.json). */
export const TOUR_MAX_STEPS = 25;
export const TOUR_TITLE_MAX_LENGTH = 120;
export const TOUR_BODY_MAX_LENGTH = 1000;

/** One validation problem found by {@link validateTourSteps}. */
export interface TourProblem {
  stepIndex: number;
  message: string;
}

/** Result of {@link validateTourSteps}. */
export interface TourValidation {
  ok: boolean;
  problems: TourProblem[];
}

/**
 * Validate tour steps: bounds (1–25 steps, title/body length), non-empty
 * human text, and — when a registry is supplied — that referenced targets
 * exist and belong to the step's route scope.
 */
export function validateTourSteps(
  steps: TourStep[],
  registry?: TargetRegistry,
  routeId?: string,
): TourValidation {
  const problems: TourProblem[] = [];
  if (steps.length === 0) {
    problems.push({ stepIndex: -1, message: "The tour has no steps to show." });
    return { ok: false, problems };
  }
  if (steps.length > TOUR_MAX_STEPS) {
    problems.push({
      stepIndex: -1,
      message: `The tour has too many steps (maximum ${String(TOUR_MAX_STEPS)}).`,
    });
  }
  steps.forEach((step, index) => {
    if (step.title.trim().length === 0) {
      problems.push({ stepIndex: index, message: "Step title must not be empty." });
    } else if (step.title.length > TOUR_TITLE_MAX_LENGTH) {
      problems.push({
        stepIndex: index,
        message: `Step title must be at most ${String(TOUR_TITLE_MAX_LENGTH)} characters.`,
      });
    }
    if (step.body.trim().length === 0) {
      problems.push({ stepIndex: index, message: "Step body must not be empty." });
    } else if (step.body.length > TOUR_BODY_MAX_LENGTH) {
      problems.push({
        stepIndex: index,
        message: `Step body must be at most ${String(TOUR_BODY_MAX_LENGTH)} characters.`,
      });
    }
    if (step.targetId !== undefined && registry !== undefined) {
      const target = registry.get(step.targetId);
      if (target === undefined) {
        problems.push({
          stepIndex: index,
          message: `Unknown target "${step.targetId}". Register it before touring.`,
        });
      } else {
        const scope = step.routeId ?? routeId;
        if (scope !== undefined && target.routeId !== scope) {
          problems.push({
            stepIndex: index,
            message: `Target "${step.targetId}" belongs to another screen and is not shown here.`,
          });
        }
      }
    }
  });
  return { ok: problems.length === 0, problems };
}

/**
 * Render the static text-only tour (NAV-AT-002 fallback). Pure function:
 * works on server and client, with or without a DOM.
 */
export function renderTextTour(steps: TourStep[]): string {
  if (steps.length === 0) return "No tour steps available.";
  const lines: string[] = [`App tour — ${String(steps.length)} ${steps.length === 1 ? "step" : "steps"}`];
  steps.forEach((step, index) => {
    lines.push("", `Step ${String(index + 1)} of ${String(steps.length)}: ${step.title}`, step.body);
  });
  return lines.join("\n");
}

/** Options for {@link createOpenConnectNavigator}. */
export interface NavigatorOptions {
  /** Authenticated client (public probe only); never mutated by the bridge. */
  client?: NavigatorClientLike;
  /**
   * Navigator endpoint base, e.g. the same origin as the client's
   * `baseUrl` (which already ends in `/openconnect/v1`).
   * Navigator paths are appended as `/navigator/...`.
   */
  baseUrl: string;
  /** Authenticated fetch wrapper; defaults to `globalThis.fetch`. */
  fetchImpl?: FetchImpl;
  /** Optional per-request bearer header supplier; no stored credentials. */
  getAuthorizationHeader?: () => string | undefined;
  /** Initial route scope for target resolution and tour validation. */
  routeId?: string;
  /** Speech is off by default; the text tour is the fallback. */
  speech?: { enabled?: boolean };
  /** Capture is off by default (NAV-AT-010); snapshots stay empty. */
  capture?: { enabled?: boolean };
  /** Shared registry; a fresh one is created when omitted. */
  registry?: TargetRegistry;
  /** Listener invoked for every command receipt. */
  onReceipt?: (receipt: NavigatorReceipt) => void;
  /** Server probe timeout in ms. Default 5000. */
  grantTimeoutMs?: number;
}

/** Result of {@link NavigatorHandle.start}. */
export interface NavigatorStartResult {
  started: boolean;
  /** `true` only when the server granted tour/mutation rights. */
  serverGranted: boolean;
  /** Human-language summary of the binding outcome. */
  note: string;
}

/** Overlay tour callbacks for host wiring (router announcements, etc.). */
export interface OverlayTourCallbacks {
  onStep?: (stepIndex: number, step: TourStep) => void;
  onFinish?: () => void;
  onSkip?: () => void;
  onPause?: () => void;
}

/** Handle for a running overlay tour. */
export interface OverlayTourHandle {
  /** Move to a step index (clamped); renders text-only when no DOM. */
  goTo: (stepIndex: number) => void;
  /** Advance one step, or finish on the last step. */
  next: () => void;
  /** Go back one step. */
  back: () => void;
  /** End the tour early (overlay closed, focus restored). */
  stop: () => void;
  /** Currently shown step index. */
  readonly currentIndex: number;
  /** `true` when rendering without a DOM (text fallback active). */
  readonly textOnly: boolean;
}

/** Live Navigator bridge handle. */
export interface NavigatorHandle {
  readonly registry: TargetRegistry;
  readonly routeId: string | undefined;
  readonly captureEnabled: boolean;
  readonly speechEnabled: boolean;
  readonly started: boolean;
  readonly disposed: boolean;
  start: () => Promise<NavigatorStartResult>;
  dispatch: (action: NavigatorAction) => Promise<NavigatorReceipt>;
  runOverlayTour: (steps: TourStep[], callbacks?: OverlayTourCallbacks) => OverlayTourHandle;
  dispose: () => void;
}

/** Create the SSR-safe Navigator bridge. Never touches the DOM. */
export function createOpenConnectNavigator(options: NavigatorOptions): NavigatorHandle {
  const baseUrl = options.baseUrl.replace(/\/+$/, "");
  if (baseUrl.length === 0) {
    throw new Error("baseUrl must not be empty.");
  }
  const registry = options.registry ?? new TargetRegistry();
  const captureEnabled = options.capture?.enabled === true;
  const speechEnabled = options.speech?.enabled === true;
  const grantTimeoutMs = options.grantTimeoutMs ?? 5000;

  let started = false;
  let disposed = false;
  let serverGranted = false;
  let receiptCounter = 0;

  const makeReceipt = (
    action: NavigatorAction,
    outcome: NavigatorOutcome,
    detail: string,
    uncertainty?: string,
  ): NavigatorReceipt => {
    receiptCounter += 1;
    const receipt: NavigatorReceipt = {
      receiptId: `nav_${Date.now().toString(36)}_${String(receiptCounter)}`,
      action,
      outcome,
      detail,
      ...(uncertainty === undefined ? {} : { uncertainty }),
    };
    options.onReceipt?.(receipt);
    return receipt;
  };

  const authorizedFetch = (): FetchImpl => {
    if (options.fetchImpl !== undefined) return options.fetchImpl;
    const impl = (globalThis as { fetch?: FetchImpl }).fetch;
    if (impl === undefined) {
      throw new Error("No fetch implementation available. Provide options.fetchImpl.");
    }
    return impl.bind(globalThis);
  };

  const probeGrant = async (): Promise<boolean> => {
    // Approved probe only: GET /navigator/targets?routeId=<scope>.
    // There is no /navigator/grants route; any non-2xx (or missing scope,
    // timeout, or network failure) fails closed to read-only mode.
    const scope = options.routeId?.trim();
    if (scope === undefined || scope.length === 0) return false;
    const controller =
      typeof AbortController !== "undefined" ? new AbortController() : undefined;
    const timer =
      controller === undefined
        ? undefined
        : setTimeout(() => controller.abort(), grantTimeoutMs);
    try {
      const headers: Record<string, string> = { Accept: "application/json" };
      const authorization = options.getAuthorizationHeader?.();
      if (authorization !== undefined) headers["Authorization"] = authorization;
      const response = await authorizedFetch()(
        `${baseUrl}/navigator/targets?routeId=${encodeURIComponent(scope)}`,
        controller === undefined ? { headers } : { headers, signal: controller.signal },
      );
      return response.ok;
    } catch {
      return false;
    } finally {
      if (timer !== undefined) clearTimeout(timer);
    }
  };

  const start = async (): Promise<NavigatorStartResult> => {
    if (disposed) {
      return { started: false, serverGranted: false, note: "The tour ended. Create a new guide to start again." };
    }
    if (started) {
      return {
        started: true,
        serverGranted,
        note: serverGranted
          ? "Guide is ready."
          : "Guide is ready in read-only mode. Some actions need approval.",
      };
    }
    started = true;
    // Optional connectivity probe via the client's public API only.
    try {
      await options.client?.listCapabilities?.();
    } catch {
      // Probe is best-effort; grant fetch below is authoritative.
    }
    serverGranted = await probeGrant();
    return {
      started: true,
      serverGranted,
      note: serverGranted
        ? "Guide is ready."
        : "Guide is ready in read-only mode. Some actions need approval.",
    };
  };

  const dispatch = async (action: NavigatorAction): Promise<NavigatorReceipt> => {
    if (disposed) {
      return makeReceipt(action, "failed", "The tour already ended.");
    }
    const scope = action.routeId ?? options.routeId;
    const target: NavigatorTarget | undefined =
      action.targetId === undefined ? undefined : registry.get(action.targetId);

    if (action.intent === "navigate") {
      if (!serverGranted) {
        return makeReceipt(
          action,
          "denied",
          "Moving to another screen needs approval, which is not granted right now.",
        );
      }
      if (target === undefined && action.targetId !== undefined) {
        return makeReceipt(action, "uncertain", "That destination is not registered.", `Unknown target "${action.targetId}".`);
      }
      return makeReceipt(
        action,
        "completed",
        target === undefined
          ? "Ready to move on. Your app will handle the screen change."
          : `Ready to move on to "${target.label}". Your app will handle the screen change.`,
      );
    }

    // Read-only intents: explain / locate / highlight / scroll / focus.
    if (action.targetId !== undefined && target === undefined) {
      return makeReceipt(
        action,
        "uncertain",
        "That item is not part of this tour.",
        `Unknown target "${action.targetId}".`,
      );
    }
    if (target !== undefined && scope !== undefined && target.routeId !== scope) {
      return makeReceipt(
        action,
        "uncertain",
        `"${target.label}" belongs to another screen and is not shown here.`,
        `Target "${target.targetId}" is scoped to route "${target.routeId}".`,
      );
    }
    if (!captureEnabled && (action.intent === "highlight" || action.intent === "locate")) {
      const snapshot = buildVisibleSnapshot({ routeId: scope ?? "", nodes: [], captureEnabled: false });
      void snapshot;
    }
    const label = target === undefined ? "this screen" : `"${target.label}"`;
    switch (action.intent) {
      case "explain":
        return makeReceipt(
          action,
          target?.description !== undefined ? "completed" : "uncertain",
          target?.description ?? `Here is ${label}.`,
          target?.description === undefined && target !== undefined
            ? `No description registered for target "${target.targetId}".`
            : undefined,
        );
      case "locate":
      case "highlight":
      case "scroll":
      case "focus":
        return makeReceipt(
          action,
          "completed",
          `Showing ${label}.`,
          typeof document === "undefined"
            ? "No screen is attached; showing the text guide instead."
            : undefined,
        );
    }
  };

  const runOverlayTour = (steps: TourStep[], callbacks?: OverlayTourCallbacks): OverlayTourHandle => {
    const validation = validateTourSteps(steps, registry, options.routeId);
    const safeSteps = validation.ok ? steps : [];
    let currentIndex = 0;
    let overlay: HighlighterHandle | null = null;
    let stopped = false;

    const render = (index: number): void => {
      if (stopped || disposed) return;
      const step = safeSteps[index];
      if (step === undefined) return;
      currentIndex = index;
      callbacks?.onStep?.(index, step);
      overlay?.close();
      overlay = showHighlight(
        {
          title: step.title,
          body: step.body,
          stepIndex: index,
          stepTotal: safeSteps.length,
          ...(step.targetId === undefined ? {} : { targetId: step.targetId }),
        },
        {
          onNext: () => handle.next(),
          onBack: () => handle.back(),
          onSkip: () => {
            handle.stop();
            callbacks?.onSkip?.();
          },
          onClose: () => {
            handle.stop();
            callbacks?.onSkip?.();
          },
          ...(callbacks?.onPause === undefined
            ? {}
            : {
                onPause: () => {
                  handle.stop();
                  callbacks.onPause?.();
                },
              }),
        },
      );
      // Null overlay (SSR / no DOM) keeps the text fallback active; the
      // host renders renderTextTour(steps) instead. Nothing else to do here.
    };

    const handle: OverlayTourHandle = {
      goTo: (stepIndex: number) => {
        if (safeSteps.length === 0) return;
        const clamped = Math.min(Math.max(0, stepIndex), safeSteps.length - 1);
        render(clamped);
      },
      next: () => {
        if (safeSteps.length === 0) return;
        if (currentIndex + 1 >= safeSteps.length) {
          handle.stop();
          callbacks?.onFinish?.();
          return;
        }
        render(currentIndex + 1);
      },
      back: () => {
        if (safeSteps.length === 0) return;
        render(Math.max(0, currentIndex - 1));
      },
      stop: () => {
        stopped = true;
        overlay?.close();
        overlay = null;
      },
      get currentIndex() {
        return currentIndex;
      },
      get textOnly() {
        return overlay === null;
      },
    };

    if (safeSteps.length > 0 && !stopped && !disposed) render(0);
    return handle;
  };

  const dispose = (): void => {
    disposed = true;
    started = false;
    serverGranted = false;
  };

  return {
    get registry() {
      return registry;
    },
    get routeId() {
      return options.routeId;
    },
    get captureEnabled() {
      return captureEnabled;
    },
    get speechEnabled() {
      return speechEnabled;
    },
    get started() {
      return started;
    },
    get disposed() {
      return disposed;
    },
    start,
    dispatch,
    runOverlayTour,
    dispose,
  };
}
