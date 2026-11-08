/**
 * `OpenConnectNavigatorProvider` — guided-tour overlays that compose (never
 * replace) the existing OpenConnect provider and `MiniComposer`.
 *
 * Nest inside (or outside) `OpenConnectProvider` from
 * `@openconnect/composer/react`; this provider owns only tour state and
 * renders its highlights/overlay as siblings of your app and the composer:
 *
 * ```tsx
 * <OpenConnectProvider client={client} sessionId={sid} agentId={aid}>
 *   <OpenConnectNavigatorProvider navigator={navigator}>
 *     <MiniComposer assistantName="Assistant" />
 *     <YourApp />
 *   </OpenConnectNavigatorProvider>
 * </OpenConnectProvider>
 * ```
 *
 * SSR-safe: importing this module touches no DOM. Subscriptions, focus
 * management, and disposal live inside effects. No duplicate root provider is
 * created — this context carries only the navigator handle.
 */
import {
  createContext,
  createElement,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { Highlighter } from "./Highlighter.js";
import { NavigatorOverlay, useDarkMode, useReducedMotion } from "./NavigatorOverlay.js";
import type { NavigatorTheme } from "./NavigatorOverlay.js";
import {
  announceFor,
  currentStep,
  type NavigatorState,
  type OpenConnectNavigator,
} from "./types.js";

export interface OpenConnectNavigatorContextValue {
  navigator?: OpenConnectNavigator;
}

const OpenConnectNavigatorContext = createContext<OpenConnectNavigatorContextValue>({});

export interface OpenConnectNavigatorProviderProps {
  /**
   * Tour handle (structural `OpenConnectNavigator`). Omit to render children
   * only — the provider is a pass-through until a navigator is attached.
   */
  navigator?: OpenConnectNavigator;
  /**
   * Dispose the navigator when the provider unmounts (logout/teardown).
   * Defaults to `true`. Set to `false` when the handle is owned elsewhere.
   */
  disposeOnUnmount?: boolean;
  /**
   * Explicit host theme forwarded to the overlay/highlighter.
   * `"light"` / `"dark"` always win; `"system"` (default) follows
   * `prefers-color-scheme`.
   */
  theme?: NavigatorTheme;
  children?: ReactNode;
}

function readSnapshot(navigator: OpenConnectNavigator | undefined): NavigatorState | null {
  if (navigator === undefined) return null;
  try {
    return navigator.getState();
  } catch {
    return null;
  }
}

/** Access the composed navigator handle (throws outside the provider). */
export function useOpenConnectNavigator(): OpenConnectNavigatorContextValue {
  return useContext(OpenConnectNavigatorContext);
}

export function OpenConnectNavigatorProvider(props: OpenConnectNavigatorProviderProps): ReactNode {
  const navigator = props.navigator;
  const disposeOnUnmount = props.disposeOnUnmount ?? true;
  const disposeRef = useRef(disposeOnUnmount);
  disposeRef.current = disposeOnUnmount;

  const [snapshot, setSnapshot] = useState<NavigatorState | null>(() => readSnapshot(navigator));
  const reducedMotion = useReducedMotion();
  const systemDark = useDarkMode();
  const theme = props.theme ?? "system";
  const dark = theme === "dark" ? true : theme === "light" ? false : systemDark;

  // Subscribe to the navigator store; unsubscribe and (by default) dispose
  // on unmount. Unmount cleanup must never throw into React.
  useEffect(() => {
    if (navigator === undefined) {
      setSnapshot(null);
      return () => {};
    }
    setSnapshot(readSnapshot(navigator));
    let unsubscribe: (() => void) | undefined;
    try {
      unsubscribe = navigator.subscribe(() => {
        setSnapshot(readSnapshot(navigator));
      });
    } catch {
      unsubscribe = undefined;
    }
    return () => {
      try {
        unsubscribe?.();
      } catch {
        // Best effort only.
      }
      if (disposeRef.current) {
        try {
          navigator.dispose();
        } catch {
          // dispose() is idempotent by contract; never throw from cleanup.
        }
      }
    };
  }, [navigator]);

  const value = useMemo<OpenConnectNavigatorContextValue>(
    () =>
      navigator === undefined
        ? {}
        : {
            navigator,
          },
    [navigator],
  );

  const step = snapshot === null ? null : currentStep(snapshot);
  const showOverlay = snapshot !== null && step !== null;

  const announcement = snapshot === null ? "Tour closed." : announceFor(snapshot);

  return createElement(
    OpenConnectNavigatorContext.Provider,
    { value },
    props.children,
    showOverlay && snapshot !== null && step !== null && navigator !== undefined
      ? createElement(Highlighter, {
          target: step.target,
          reducedMotion,
          dark,
          key: `oc-nav-highlight-${step.id}`,
        })
      : null,
    showOverlay && snapshot !== null && step !== null && navigator !== undefined
      ? createElement(NavigatorOverlay, {
          navigator,
          state: snapshot,
          step,
          announcement,
          theme,
          key: `oc-nav-overlay-${step.id}`,
        })
      : null,
  );
}
