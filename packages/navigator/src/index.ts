/**
 * `@openconnect/navigator` — framework-neutral SSR-safe Navigator bridge.
 *
 * Importing this entry is DOM-free: no `window`, `document`, or `navigator`
 * access happens at import time or in `createOpenConnectNavigator()`. The
 * DOM is bound only inside `start()` / `runOverlayTour()` (browser only),
 * and `dispose()` releases everything the bridge created.
 *
 * The bridge never alters `@openconnect/client` or MiniComposer
 * registration: the client is used through its public surface only, and
 * only as an optional connectivity probe.
 *
 * Pair with `styles.css` (import `"@openconnect/navigator/styles.css"` in
 * bundlers) for the style-isolated `ocnav-` overlay theme tokens.
 */
export { hasDom, showHighlight, tourProgressText, shouldCloseOnKey } from "./highlighter.js";
export type {
  HighlightControls,
  HighlightRequest,
  HighlighterHandle,
} from "./highlighter.js";
export {
  createOpenConnectNavigator,
  renderTextTour,
  validateTourSteps,
  TOUR_BODY_MAX_LENGTH,
  TOUR_MAX_STEPS,
  TOUR_TITLE_MAX_LENGTH,
} from "./navigator.js";
export type {
  FetchImpl,
  GrantRequiredIntent,
  NavigatorAction,
  NavigatorClientLike,
  NavigatorHandle,
  NavigatorIntent,
  NavigatorOptions,
  NavigatorOutcome,
  NavigatorReceipt,
  NavigatorStartResult,
  OverlayTourCallbacks,
  OverlayTourHandle,
  ReadonlyIntent,
  TourProblem,
  TourStep,
  TourValidation,
} from "./navigator.js";
export {
  buildVisibleSnapshot,
  collectVisibleNodes,
  isSensitiveField,
  sanitizeNodeText,
  NAVIGATOR_SCHEMA_VERSION,
  REDACTED_PLACEHOLDER,
  SNAPSHOT_MAX_NODES,
  SNAPSHOT_MAX_TEXT_LENGTH,
} from "./observation.js";
export type {
  BuildSnapshotInput,
  RawSnapshotNode,
  SnapshotNode,
  VisibleSnapshot,
  WalkableElement,
} from "./observation.js";
export {
  TargetRegistry,
  TargetError,
  ROUTE_ID_MAX_LENGTH,
  TARGET_DESCRIPTION_MAX_LENGTH,
  TARGET_ID_MAX_LENGTH,
  TARGET_LABEL_MAX_LENGTH,
} from "./targets.js";
export type { NavigatorTarget, RegisterTargetInput } from "./targets.js";
