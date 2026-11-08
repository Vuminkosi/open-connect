/**
 * Native highlighter overlay for the Navigator bridge.
 *
 * Importing this module is DOM-free and SSR-safe: every `document`/`window`
 * access happens inside {@link showHighlight}, which returns `null` outside
 * a browser (callers fall back to the static text tour). `dispose`/close
 * removes all listeners and nodes and restores focus to the element that
 * held it before the overlay opened.
 *
 * Styling uses the style-isolated `ocnav-` class prefix (see
 * `src/styles.css`); critical positioning is applied inline so the overlay
 * works even when the stylesheet is not bundled. The overlay honours
 * `prefers-reduced-motion` and right-to-left layouts.
 */

/** One overlay step shown by the highlighter. */
export interface HighlightRequest {
  /** Stable target id; resolved via `[data-navigator-target]` or id. */
  targetId?: string;
  /** Short caption shown in the overlay card. */
  title: string;
  /** Plain-language body (human language only). */
  body: string;
  /** Zero-based step index for progress. */
  stepIndex: number;
  /** Total step count for progress. */
  stepTotal: number;
}

/** Host callbacks wired to overlay controls. */
export interface HighlightControls {
  onNext: () => void;
  onBack: () => void;
  onSkip: () => void;
  onClose: () => void;
  onPause?: () => void;
}

/** Handle for the open overlay. */
export interface HighlighterHandle {
  /** Which element (if any) the overlay is anchored to. */
  readonly anchored: boolean;
  /** Remove the overlay, listeners, and restore previous focus. */
  close: () => void;
}

/** Returns `true` when running in a browser with a usable DOM. */
export function hasDom(): boolean {
  return (
    typeof document !== "undefined" &&
    typeof window !== "undefined" &&
    typeof document.createElement === "function"
  );
}

/** Pure progress caption: "Step 2 of 5". */
export function tourProgressText(stepIndex: number, stepTotal: number): string {
  return `Step ${String(stepIndex + 1)} of ${String(stepTotal)}`;
}

/** Returns `true` when a keydown should dismiss the overlay (Escape). */
export function shouldCloseOnKey(key: string): boolean {
  return key === "Escape";
}

/**
 * Show the highlight overlay for one tour step. Returns `null` when no DOM
 * is available (SSR / text-only fallback). Anchors a focus ring to the
 * target element when found; otherwise shows an unanchored card and lets
 * the caller record uncertainty.
 */
export function showHighlight(
  request: HighlightRequest,
  controls: HighlightControls,
): HighlighterHandle | null {
  if (!hasDom()) return null;
  const doc = document;
  const previouslyFocused =
    doc.activeElement instanceof HTMLElement ? doc.activeElement : null;

  const reducedMotion =
    typeof window.matchMedia === "function" &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const isRtl = doc.documentElement.getAttribute("dir") === "rtl";

  const layer = doc.createElement("div");
  layer.setAttribute("class", "ocnav-layer");

  const ring = doc.createElement("div");
  ring.setAttribute("class", "ocnav-ring");
  ring.setAttribute("aria-hidden", "true");
  layer.appendChild(ring);

  const card = doc.createElement("div");
  card.setAttribute("class", "ocnav-card");
  card.setAttribute("role", "dialog");
  card.setAttribute("aria-modal", "false");
  card.setAttribute("aria-label", `${request.title}. ${tourProgressText(request.stepIndex, request.stepTotal)}`);
  if (isRtl) card.setAttribute("dir", "rtl");

  const progress = doc.createElement("p");
  progress.setAttribute("class", "ocnav-progress");
  progress.textContent = tourProgressText(request.stepIndex, request.stepTotal);

  const heading = doc.createElement("h2");
  heading.setAttribute("class", "ocnav-title");
  heading.setAttribute("id", "ocnav-title");
  heading.setAttribute("tabindex", "-1");
  heading.textContent = request.title;

  const body = doc.createElement("p");
  body.setAttribute("class", "ocnav-body");
  body.setAttribute("id", "ocnav-desc");
  body.textContent = request.body;
  card.setAttribute("aria-describedby", "ocnav-desc");

  const actions = doc.createElement("div");
  actions.setAttribute("class", "ocnav-actions");

  const makeButton = (label: string, onClick: () => void, primary: boolean): HTMLButtonElement => {
    const button = doc.createElement("button");
    button.setAttribute("type", "button");
    button.setAttribute("class", primary ? "ocnav-btn ocnav-btn-primary" : "ocnav-btn");
    button.textContent = label;
    button.addEventListener("click", onClick);
    return button;
  };

  const backButton = makeButton("Back", controls.onBack, false);
  backButton.disabled = request.stepIndex <= 0;
  const nextLabel = request.stepIndex + 1 >= request.stepTotal ? "Finish" : "Next";
  const nextButton = makeButton(nextLabel, controls.onNext, true);
  const skipButton = makeButton("Skip tour", controls.onSkip, false);
  const pauseButton =
    controls.onPause === undefined ? null : makeButton("Pause", controls.onPause, false);
  actions.append(backButton, nextButton);
  if (pauseButton !== null) actions.appendChild(pauseButton);
  actions.appendChild(skipButton);

  card.append(progress, heading, body, actions);
  layer.appendChild(card);
  doc.body.appendChild(layer);

  let anchored = false;
  const placeRing = (): void => {
    const target = findTargetElement(doc, request.targetId);
    if (target === null) {
      ring.style.display = "none";
      anchored = false;
      return;
    }
    anchored = true;
    const rect = target.getBoundingClientRect();
    const pad = 6;
    ring.style.display = "block";
    ring.style.left = `${String(Math.max(0, rect.left + window.scrollX - pad))}px`;
    ring.style.top = `${String(Math.max(0, rect.top + window.scrollY - pad))}px`;
    ring.style.width = `${String(rect.width + pad * 2)}px`;
    ring.style.height = `${String(rect.height + pad * 2)}px`;
    try {
      target.scrollIntoView({ block: "nearest", behavior: reducedMotion ? "auto" : "smooth" });
    } catch {
      // scrollIntoView is best-effort; the text fallback carries the content.
    }
  };
  placeRing();

  const onKeyDown = (event: KeyboardEvent): void => {
    if (shouldCloseOnKey(event.key)) {
      event.preventDefault();
      controls.onClose();
      return;
    }
    // Keep Tab inside the dialog card so focus never leaves the overlay
    // while it is open (the card is the role="dialog" element).
    if (event.key === "Tab") {
      const order = [backButton, nextButton, ...(pauseButton === null ? [] : [pauseButton]), skipButton].filter(
        (button) => !button.disabled,
      );
      if (order.length === 0) {
        event.preventDefault();
        return;
      }
      const active = doc.activeElement as unknown as HTMLButtonElement | null;
      const index = order.indexOf(active as HTMLButtonElement);
      if (event.shiftKey) {
        if (index <= 0) {
          event.preventDefault();
          order[order.length - 1]?.focus({ preventScroll: true });
        }
      } else if (index === -1 || index >= order.length - 1) {
        event.preventDefault();
        order[0]?.focus({ preventScroll: true });
      }
    }
  };
  const onResize = (): void => {
    placeRing();
  };
  doc.addEventListener("keydown", onKeyDown);
  window.addEventListener("resize", onResize);

  // Initial focus lands on the heading inside the dialog card (NAV-UX-02):
  // the card carries role="dialog", so focus stays within the dialog and
  // screen readers announce the step title + progress.
  heading.focus({ preventScroll: true });

  let closed = false;
  const close = (): void => {
    if (closed) return;
    closed = true;
    doc.removeEventListener("keydown", onKeyDown);
    window.removeEventListener("resize", onResize);
    layer.remove();
    if (previouslyFocused !== null && doc.contains(previouslyFocused)) {
      previouslyFocused.focus({ preventScroll: true });
    }
  };

  return {
    get anchored() {
      return anchored;
    },
    close,
  };
}

/** Resolve a target id to a visible element: `[data-navigator-target]` then id. */
function findTargetElement(doc: Document, targetId: string | undefined): HTMLElement | null {
  if (targetId === undefined || targetId.length === 0) return null;
  const byAttribute = doc.querySelector(`[data-navigator-target="${cssEscape(targetId)}"]`);
  if (byAttribute instanceof HTMLElement) return byAttribute;
  const byId = doc.getElementById(targetId);
  return byId instanceof HTMLElement ? byId : null;
}

/** Minimal CSS.escape fallback (native CSS.escape is used when present). */
function cssEscape(value: string): string {
  if (typeof CSS !== "undefined" && typeof CSS.escape === "function") return CSS.escape(value);
  return value.replace(/["\\]/g, "\\$&");
}
