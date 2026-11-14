const highlights = new Set();
const idAttribute = "data-openconnect-liveui-id";
const highlightAttribute = "data-openconnect-liveui-highlight";
const sensitiveFieldPattern = /passw|passwd|secret|token|api[-_]?key|ssn|social.security|credit|card|cvv|cvc|credential|private.key|pin/i;

export function execute(context, timeoutMs, enableRuntimeFallback) {
  const { manifest, route, target, action } = context;
  if (!manifest || !route || !action || action.schemaVersion !== "openconnect.liveui/v1") return result("Failed", "invalid_request");
  if (normalizePath(location.pathname) !== normalizePath(new URL(route.path, document.baseURI).pathname)) return result("Conflict", "route_mismatch");
  if (action.type === "ClearHighlight") {
    clearHighlights();
    return result("Completed", "highlight_cleared", true, "cleared");
  }
  if (!target) return result("Failed", "target_missing");
  const resolution = resolveTarget(target, enableRuntimeFallback, route);
  if (resolution.status === "ambiguous") return result("Ambiguous", "target_ambiguous");
  if (resolution.status !== "resolved") return result("MissingTarget", "target_missing");

  const element = resolution.element;
  const timeout = Math.max(1, Math.min(Number(timeoutMs) || 10000, 120000));
  const parameters = action.parameters || {};
  switch (action.type) {
    case "Open":
    case "Close":
    case "Click":
      element.click();
      if (parameters.stateName) return waitForState(element, parameters.stateName, parameters.stateValue, timeout);
      if (action.type !== "Click") return waitForState(element, "expanded", action.type === "Open" ? "true" : "false", timeout);
      return result("Uncertain", "click_dispatched_without_postcondition");
    case "Focus":
      element.focus({ preventScroll: true });
      return document.activeElement === element ? result("Completed", "focus_verified", true, "focused") : result("Uncertain", "focus_unverified");
    case "Scroll":
      element.scrollIntoView({ block: parameters.scrollAlignment || "nearest", behavior: prefersReducedMotion() ? "auto" : "smooth" });
      return waitForVisibility(element, timeout);
    case "Highlight":
      addHighlight(element);
      element.scrollIntoView({ block: "nearest", behavior: prefersReducedMotion() ? "auto" : "smooth" });
      return result("Completed", "highlight_active", true, "highlighted");
    case "SetValue": return setValue(element, parameters.value);
    case "Select": return selectValue(element, parameters.value);
    case "SelectTab":
      element.click();
      return waitForState(element, "selected", "true", timeout);
    case "Inspect": return inspect(element);
    case "WaitForState":
      if (!parameters.stateName) return result("Failed", "state_name_required");
      return waitForState(element, parameters.stateName, parameters.stateValue, timeout);
    default: return result("Failed", "action_unsupported");
  }
}

export function awaitRoute(expectedPath, route, timeoutMs, enableRuntimeFallback) {
  const matches = () => {
    if (normalizePath(location.pathname) !== normalizePath(expectedPath)) return false;
    if (route && route.targets && route.targets.length) {
      return route.targets.some((target) => resolveTarget(target, enableRuntimeFallback, route).status === "resolved");
    }
    return !route || !route.title || document.title === route.title;
  };
  return waitUntil(matches, timeoutMs, "route_ready", "route_not_ready");
}

function resolveTarget(target, enableRuntimeFallback, route, visited = new Set()) {
  if (visited.has(target.id)) return { status: "ambiguous" };
  visited.add(target.id);

  let scope = document;
  if (target.parentId) {
    const parent = route?.targets?.find((candidate) => candidate.id === target.parentId);
    if (!parent) return { status: "missing" };
    const parentResolution = resolveTarget(parent, enableRuntimeFallback, route, visited);
    if (parentResolution.status !== "resolved") return parentResolution;
    scope = parentResolution.element;
  }

  const marked = [...scope.querySelectorAll("[" + idAttribute + "]")]
    .filter((element) => element.getAttribute(idAttribute) === target.id && isVisible(element));
  if (marked.length > 1) return { status: "ambiguous" };
  if (marked.length === 1) return { status: "resolved", element: marked[0] };
  if (target.selector) {
    try {
      const selected = [...scope.querySelectorAll(target.selector)].filter(isVisible);
      if (selected.length > 1) return { status: "ambiguous" };
      if (selected.length === 1) return { status: "resolved", element: selected[0] };
    } catch {
      // Invalid compiler output falls through to semantic resolution.
    }
  }
  if (!enableRuntimeFallback) return { status: "missing" };
  const label = normalize(target.label);
  if (!label) return { status: "missing" };
  const matches = [...scope.querySelectorAll("*")]
    .filter(isVisible)
    .filter((element) => kindMatches(target.kind, element))
    .filter((element) => (!target.role || roleOf(element) === target.role) &&
      accessibleNames(element).some((name) => normalize(name) === label));
  if (matches.length > 1) return { status: "ambiguous" };
  return matches.length === 1 ? { status: "resolved", element: matches[0] } : { status: "missing" };
}

function kindMatches(kind, element) {
  const tag = element.tagName.toLowerCase();
  switch (kind) {
    case "Button": return tag === "button" || element.getAttribute("role") === "button";
    case "Link": return tag === "a" || element.getAttribute("role") === "link";
    case "Input": return tag === "input" || tag === "textarea";
    case "Select": return tag === "select";
    case "Checkbox": return tag === "input" && element.type === "checkbox";
    case "Tab": return roleOf(element) === "tab";
    case "Heading": return /^h[1-6]$/.test(tag) || roleOf(element) === "heading";
    case "Region": return ["main", "section", "nav", "aside", "article"].includes(tag) || roleOf(element) === "region";
    case "List": return ["ul", "ol"].includes(tag) || ["list", "listbox"].includes(roleOf(element));
    case "Table": return tag === "table" || ["grid", "table"].includes(roleOf(element));
    default: return false;
  }
}

function roleOf(element) {
  if (element.getAttribute("role")) return element.getAttribute("role");
  const tag = element.tagName.toLowerCase();
  if (tag === "button") return "button";
  if (tag === "a" && element.hasAttribute("href")) return "link";
  if (/^h[1-6]$/.test(tag)) return "heading";
  if (tag === "input") return element.type === "checkbox" ? "checkbox" : "textbox";
  if (tag === "textarea") return "textbox";
  if (tag === "select") return "combobox";
  return null;
}

function accessibleNames(element) {
  const names = [];
  if (element.getAttribute("aria-label")) names.push(element.getAttribute("aria-label"));
  const labelledBy = element.getAttribute("aria-labelledby");
  if (labelledBy) names.push(labelledBy.split(/\s+/).map((id) => document.getElementById(id)?.textContent || "").join(" "));
  if (element.labels) for (const label of element.labels) names.push(label.textContent || "");
  for (const attribute of ["placeholder", "title", "alt"]) {
    const value = element.getAttribute(attribute);
    if (value) names.push(value);
  }
  if (element.innerText || element.textContent) names.push(element.innerText || element.textContent);
  return names;
}

function isVisible(element) {
  if (!element.isConnected || element.hidden || element.closest("[hidden], [inert], [aria-hidden='true']")) return false;
  const style = getComputedStyle(element);
  return style.display !== "none" && style.visibility !== "hidden" && style.visibility !== "collapse" &&
    element.getClientRects().length > 0;
}

function setValue(element, value) {
  if (typeof value !== "string" || !("value" in element)) return result("Failed", "value_not_supported");
  if (element.type === "password" || element.hasAttribute("data-sensitive") ||
      sensitiveFieldPattern.test((element.name || "") + " " + (element.id || ""))) {
    return result("Denied", "sensitive_input_blocked");
  }
  const prototype = element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
  if (!setter) return result("Failed", "value_setter_unavailable");
  setter.call(element, value);
  element.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
  element.dispatchEvent(new Event("change", { bubbles: true }));
  return element.value === value ? result("Completed", "value_set", true, "value_set") : result("Uncertain", "value_unverified");
}

function selectValue(element, value) {
  if (!(element instanceof HTMLSelectElement) || typeof value !== "string") return result("Failed", "select_not_supported");
  const option = [...element.options].find((item) => item.value === value || normalize(item.label) === normalize(value));
  if (!option) return result("MissingTarget", "option_missing");
  element.value = option.value;
  element.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
  element.dispatchEvent(new Event("change", { bubbles: true }));
  return element.value === option.value ? result("Completed", "selection_verified", true, "selected") : result("Uncertain", "selection_unverified");
}

function inspect(element) {
  const role = roleOf(element);
  if (role === "tab" || role === "radio") {
    const selected = element.getAttribute("aria-selected") === "true";
    return result("Completed", "inspection_verified", true, selected ? "tab_selected" : "tab_unselected");
  }
  if ("checked" in element) return result("Completed", "inspection_verified", true, element.checked ? "control_checked" : "control_unchecked");
  return result("Completed", "inspection_verified", true, "control_visible");
}

function waitForState(element, name, expected, timeoutMs) {
  const matches = () => {
    switch (name) {
      case "selected": return (element.getAttribute("aria-selected") || element.getAttribute("aria-pressed")) === (expected || "true");
      case "checked": return "checked" in element && String(element.checked) === (expected || "true");
      case "expanded": return element.getAttribute("aria-expanded") === (expected || "true");
      case "visible": return isVisible(element) === (expected !== "false");
      case "exists": return element.isConnected === (expected !== "false");
      case "textContains": return typeof expected === "string" && normalize(element.innerText || element.textContent || "").includes(normalize(expected));
      case "textNotContains": return typeof expected === "string" && !normalize(element.innerText || element.textContent || "").includes(normalize(expected));
      default: return false;
    }
  };
  return waitUntil(matches, timeoutMs, "state_verified", "state_not_verified");
}

function waitForVisibility(element, timeoutMs) {
  return waitUntil(() => {
    const bounds = element.getBoundingClientRect();
    return bounds.bottom > 0 && bounds.right > 0 && bounds.top < innerHeight && bounds.left < innerWidth;
  }, timeoutMs, "scroll_verified", "scroll_unverified");
}

function waitUntil(predicate, timeoutMs, successCode, timeoutCode) {
  if (predicate()) return Promise.resolve(result("Completed", successCode, true, successCode));
  return new Promise((resolve) => {
    let finished = false;
    const observer = new MutationObserver(check);
    const timer = setTimeout(() => finish(false), timeoutMs);
    function check() { if (predicate()) finish(true); }
    function finish(verified) {
      if (finished) return;
      finished = true;
      observer.disconnect();
      clearTimeout(timer);
      resolve(verified ? result("Completed", successCode, true, successCode) : result("Uncertain", timeoutCode));
    }
    observer.observe(document.documentElement, { attributes: true, childList: true, subtree: true, characterData: true });
    check();
  });
}

function addHighlight(element) {
  installHighlightStyles();
  element.setAttribute(highlightAttribute, "true");
  highlights.add(element);
}

function clearHighlights() {
  for (const element of highlights) element.removeAttribute(highlightAttribute);
  highlights.clear();
}

function installHighlightStyles() {
  if (document.getElementById("openconnect-liveui-style")) return;
  const style = document.createElement("style");
  style.id = "openconnect-liveui-style";
  style.textContent = [
    ":root { --oc-liveui-focus: #2563eb; }",
    "[" + highlightAttribute + "=\"true\"] { outline: 3px solid var(--oc-liveui-focus); outline-offset: 3px; box-shadow: 0 0 0 5px rgba(37, 99, 235, 0.18); }",
    "@media (prefers-reduced-motion: no-preference) { [" + highlightAttribute + "=\"true\"] { transition: outline-color 120ms ease, box-shadow 120ms ease; } }",
  ].join("\n");
  document.head.append(style);
}

function prefersReducedMotion() {
  return matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function normalize(value) {
  return String(value).replace(/\s+/g, " ").trim().toLocaleLowerCase();
}

function normalizePath(value) {
  const path = value.split(/[?#]/, 1)[0] || "/";
  return path.length > 1 ? path.replace(/\/+$/, "") : "/";
}

function result(outcome, code, postconditionVerified = false, verifiedState = null) {
  return { outcome, code, postconditionVerified, verifiedState };
}
