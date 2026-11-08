/**
 * `@openconnect/navigator-react` — React provider + overlays for OpenConnect
 * guided tours. Composes (never replaces) the existing OpenConnect provider
 * and `MiniComposer` from `@openconnect/composer/react`.
 *
 * Importing this entry is DOM-free and SSR-safe: no `document`, `window`, or
 * `matchMedia` access happens at import time. All DOM work lives inside
 * effects with `typeof` guards; server rendering produces static markup and
 * hydration attaches behaviour on the client.
 */
export { OpenConnectNavigatorProvider, useOpenConnectNavigator } from "./NavigatorProvider.js";
export type {
  OpenConnectNavigatorContextValue,
  OpenConnectNavigatorProviderProps,
} from "./NavigatorProvider.js";
export { Highlighter } from "./Highlighter.js";
export type { HighlighterProps } from "./Highlighter.js";
export { NavigatorOverlay } from "./NavigatorOverlay.js";
export type { NavigatorOverlayProps, NavigatorTheme } from "./NavigatorOverlay.js";
export { announceFor, currentStep } from "./types.js";
export type {
  NavigatorListener,
  NavigatorState,
  NavigatorStatus,
  NavigatorStep,
  OpenConnectNavigator,
} from "./types.js";
