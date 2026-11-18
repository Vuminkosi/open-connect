const highlights = new Set();
const highlightTimers = new Map();
const pendingRoutes = new Map();
let nextRouteObservation = 0;
const idAttribute = "data-openconnect-liveui-id";
const highlightAttribute = "data-openconnect-liveui-highlight";
const sensitiveFieldPattern = /password|passwd|secret|token|api[\s_-]?key|\bssn\b|social[\s._-]?security|credit.?card|credit|card|cvv|cvc|credential|private[\s._-]?key|\bpin\b|\bpin[\s_-]?(?:code|number)\b/i;
const sensitiveAutocompletePattern = /^(cc-|current-password$|new-password$|one-time-code$)/i;

// Return bounded control labels and roles, never control values or code examples.
export function discoverTargets(maximumTargets) {
  const limit = Math.max(0, Math.min(Number(maximumTargets) || 0, 500));
  if (!limit) return [];
  const selector = "button, a[href], input, textarea, select, h1, h2, h3, h4, h5, h6, [role='button'], [role='link'], [role='tab'], [role='heading']";
  const sections = document.querySelectorAll("section");
  const regionCandidates = [];
  for (let index = 0; index < Math.min(sections.length, 5000); index++) {
    const section = sections[index];
    if (!isVisible(section) || section.closest("pre, code")) continue;
    const explicitName = roleOf(section) === "region" ? accessibleNames(section).slice(0, 1) : [];
    const headingName = section.querySelector(":scope > h1, :scope > h2, :scope > h3, :scope > h4, :scope > h5, :scope > h6");
    const label = [...explicitName, headingName && isVisible(headingName) ? headingName.innerText : ""].map((name) => String(name).replace(/\s+/g, " ").trim())
      .find((name) => name.length > 0 && name.length <= 120);
    if (label && !sensitiveFieldPattern.test(label)) regionCandidates.push({ element: section, label, role: roleOf(section) });
  }
  const regionCounts = new Map();
  for (const region of regionCandidates) regionCounts.set(normalize(region.label), (regionCounts.get(normalize(region.label)) || 0) + 1);
  const regions = regionCandidates.filter((region) => regionCounts.get(normalize(region.label)) === 1).slice(0, limit);
  const regionLabels = new Map(regions.map((region) => [region.element, region.label]));
  const candidates = [];
  const elements = document.querySelectorAll(selector);
  for (let index = 0; index < Math.min(elements.length, 5000); index++) {
    const element = elements[index];
    if (!isVisible(element) || element.closest("pre, code") || isDisabled(element)) continue;
    const tag = element.tagName.toLowerCase();
    const role = roleOf(element);
    if (tag === "input" && element.type === "radio") continue;
    const kind = role === "tab" ? "Tab" : role === "button" ? "Button" : role === "link" ? "Link" :
      role === "heading" ? "Heading" : tag === "select" ? "Select" :
      tag === "input" && element.type === "checkbox" ? "Checkbox" :
      tag === "input" || tag === "textarea" ? "Input" : null;
    if (!kind || kind === "Link" && !isAllowedLinkActivation(element)) continue;
    if (["Input", "Checkbox", "Select"].includes(kind) && isSensitiveField(element)) continue;
    const label = accessibleNames(element).map((name) => String(name).replace(/\s+/g, " ").trim())
      .find((name) => name.length > 0 && name.length <= 120);
    if (!label || sensitiveFieldPattern.test(label)) continue;
    const parentLabel = regionLabels.get(element.closest("section"));
    const candidate = parentLabel ? { kind, label, role, parentLabel } : { kind, label, role };
    if (kind === "Link") {
      const link = element.closest("a[href]");
      if (link) {
        const destination = new URL(link.getAttribute("href"), document.baseURI);
        if (!destination.search && !destination.hash) candidate.path = destination.pathname;
      }
    }
    candidates.push(candidate);
  }
  const counts = new Map();
  for (const candidate of candidates) {
    const key = `${candidate.kind}\0${candidate.role || ""}\0${normalize(candidate.label)}\0${normalize(candidate.parentLabel || "")}`;
    counts.set(key, (counts.get(key) || 0) + 1);
  }
  const unique = candidates.filter((candidate) =>
    counts.get(`${candidate.kind}\0${candidate.role || ""}\0${normalize(candidate.label)}\0${normalize(candidate.parentLabel || "")}`) === 1);
  return [...regions.map((region) => ({ kind: "Region", label: region.label, role: region.role })), ...unique].slice(0, limit);
}

export function execute(context, timeoutMs, enableRuntimeFallback) {
  const { manifest, route, target, action } = context;
  if (!manifest || !route || !action || action.schemaVersion !== "openconnect.liveui/v1") return result("Failed", "invalid_request");
  if (normalizePath(location.pathname) !== normalizePath(new URL(route.path, document.baseURI).pathname)) return result("Conflict", "route_mismatch");
  if (action.type === "ClearHighlight") {
    const actionStartedAt = performance.now();
    clearHighlights();
    return withMeasurements(result("Completed", "highlight_cleared", true, "cleared"), 0, actionStartedAt);
  }
  if (!target) return result("Failed", "target_missing");
  const resolutionStartedAt = performance.now();
  const resolution = resolveTarget(target, enableRuntimeFallback, route);
  const targetResolutionMilliseconds = performance.now() - resolutionStartedAt;
  const actionStartedAt = performance.now();
  const measured = (value) => withMeasurements(value, targetResolutionMilliseconds, actionStartedAt);
  if (resolution.status === "ambiguous") return measured(result("Ambiguous", "target_ambiguous"));
  if (resolution.status !== "resolved") return measured(result("MissingTarget", "target_missing"));

  const element = resolution.element;
  if (element.closest("pre, code") && !["Inspect", "Highlight", "Scroll", "Focus", "WaitForState"].includes(action.type))
    return measured(result("Denied", "code_content_not_interactive"));
  const timeout = Math.max(1, Math.min(Number(timeoutMs) || 10000, 120000));
  const parameters = action.parameters || {};
  const verifyState = (name, expected) => waitForState(target, route, enableRuntimeFallback, name, expected, timeout);
  const isCurrentTarget = () => {
    if (normalizePath(location.pathname) !== normalizePath(new URL(route.path, document.baseURI).pathname)) return false;
    const current = resolveTarget(target, enableRuntimeFallback, route);
    return current.status === "resolved" && current.element === element && element.isConnected;
  };
  switch (action.type) {
    case "Open":
    case "Close":
    case "Click":
      if (isDisabled(element)) return measured(result("Denied", "control_disabled"));
      if (!isAllowedLinkActivation(element)) return measured(result("Denied", "navigation_boundary"));
      element.click();
      if (parameters.stateName) return measured(verifyState(parameters.stateName, parameters.stateValue));
      if (action.type !== "Click") return measured(verifyState("expanded", action.type === "Open" ? "true" : "false"));
      return measured(result("Uncertain", "click_dispatched_without_postcondition"));
    case "Focus":
      element.focus({ preventScroll: true });
      return measured(document.activeElement === element ? result("Completed", "focus_verified", true, "focused") : result("Uncertain", "focus_unverified"));
    case "Scroll":
      element.scrollIntoView({ block: parameters.scrollAlignment || "nearest", behavior: prefersReducedMotion() ? "auto" : "smooth" });
      return measured(waitForVisibility(element, timeout));
    case "Highlight": {
      let highlightTarget = element;
      if (parameters.value !== undefined && parameters.value !== null) {
        const match = resolveTextMatchTarget(element, target, parameters.value);
        if (match.status === "ambiguous") return measured(result("Ambiguous", "text_match_ambiguous"));
        if (match.status === "unsupported") return measured(result("Failed", "text_match_unsupported"));
        if (match.status !== "resolved") return measured(result("MissingTarget", "text_match_missing"));
        highlightTarget = match.element;
      }
      addHighlight(highlightTarget);
      highlightTarget.scrollIntoView({ block: "nearest", behavior: prefersReducedMotion() ? "auto" : "smooth" });
      const highlightDuration = Number(parameters.highlightDurationMilliseconds);
      if (Number.isInteger(highlightDuration) && highlightDuration > 0) {
        const previousTimer = highlightTimers.get(highlightTarget);
        if (previousTimer) clearTimeout(previousTimer);
        highlightTimers.set(highlightTarget, setTimeout(() => clearHighlight(highlightTarget), Math.min(highlightDuration, 30_000)));
        return measured(result("Completed", "highlight_temporary", true, "highlighted"));
      }
      return measured(result("Completed", "highlight_active", true, "highlighted"));
    }
    case "SetValue": return measured(setValue(element, parameters.value, isCurrentTarget));
    case "Select": return measured(selectValue(element, parameters.value, isCurrentTarget));
    case "SelectTab":
      if (isDisabled(element)) return measured(result("Denied", "control_disabled"));
      element.click();
      return measured(verifyState("selected", "true"));
    case "Inspect": return measured(inspect(element));
    case "WaitForState":
      if (!parameters.stateName) return measured(result("Failed", "state_name_required"));
      return measured(verifyState(parameters.stateName, parameters.stateValue));
    default: return measured(result("Failed", "action_unsupported"));
  }
}

function isAllowedLinkActivation(element) {
  const link = element.closest("a[href]");
  if (!link) return true;
  if (link.hasAttribute("download")) return false;
  const target = (link.getAttribute("target") || document.querySelector("base[target]")?.getAttribute("target") || "_self").trim().toLowerCase();
  if (target !== "_self") return false;
  try {
    const destination = new URL(link.getAttribute("href"), document.baseURI);
    return (destination.protocol === "http:" || destination.protocol === "https:") && destination.origin === location.origin;
  } catch {
    return false;
  }
}

function resolveTextMatchTarget(container, target, matchText) {
  if (target?.kind !== "List" && target?.kind !== "Table") return { status: "unsupported" };
  if (typeof matchText !== "string" || !matchText.trim() || matchText.length > 256) return { status: "missing" };
  const selector = target.kind === "Table" ? "tr" : "li, [role='listitem'], .mud-list-item";
  const query = normalize(matchText);
  const matches = [...container.querySelectorAll(selector)]
    .filter(isVisible)
    .filter((item) => normalize(item.innerText || "").includes(query));
  const mostSpecificMatches = matches.filter((item) =>
    !matches.some((candidate) => candidate !== item && item.contains(candidate)));
  if (mostSpecificMatches.length > 1) return { status: "ambiguous" };
  return mostSpecificMatches.length === 1
    ? { status: "resolved", element: mostSpecificMatches[0] }
    : { status: "missing" };
}

function matchingRouteHeadings(title) {
  const label = normalize(title || "");
  if (!label) return [];
  const containers = [...document.querySelectorAll("main, [role='main']")].filter(isVisible);
  const roots = containers.length ? containers : [document];
  return roots.flatMap((root) => [...root.querySelectorAll("h1,h2,h3,h4,h5,h6,[role='heading']")])
    .filter((heading) => isVisible(heading) && !heading.closest("pre, code"))
    .filter((heading) => accessibleNames(heading).some((name) => normalize(name) === label));
}

function matchingRouteLinks(route, parentLabel = null) {
  return [...document.querySelectorAll("a[href]")].filter((link) => {
    if (!isVisible(link) || isDisabled(link) || !isAllowedLinkActivation(link) || link.closest("pre, code")) return false;
    if (parentLabel) {
      const section = link.closest("section");
      if (!section || !isVisible(section) || !accessibleNames(section).some((name) => normalize(name) === normalize(parentLabel))) return false;
    }
    const destination = new URL(link.getAttribute("href"), document.baseURI);
    return !destination.search && !destination.hash && destination.pathname === route.path &&
      accessibleNames(link).some((name) => normalize(name) === normalize(route.title));
  });
}

export function prepareRoute(route, parentLabel = null) {
  if (!route?.id?.startsWith("oc.runtime.route.") || typeof route.title !== "string" || !route.title.trim() || pendingRoutes.size >= 32) return null;
  if (parentLabel !== null && (typeof parentLabel !== "string" || !parentLabel.trim() || parentLabel.length > 120)) return null;
  if (matchingRouteLinks(route, parentLabel).length !== 1) return null;
  const token = String(++nextRouteObservation);
  const timer = setTimeout(() => pendingRoutes.delete(token), 120000);
  pendingRoutes.set(token, { routeId: route.id, path: route.path, title: route.title,
    sourcePath: location.pathname, parentLabel, dispatched: false, previousHeadings: new Set(matchingRouteHeadings(route.title)), timer });
  return token;
}

export function followRoute(route, observationToken) {
  const observation = pendingRoutes.get(observationToken);
  if (!observation || observation.dispatched || observation.sourcePath !== location.pathname ||
      observation.routeId !== route?.id || observation.path !== route.path || observation.title !== route.title) return false;
  const links = matchingRouteLinks(route, observation.parentLabel);
  if (links.length !== 1) return false;
  observation.dispatched = true;
  links[0].click();
  return true;
}

export function awaitRoute(expectedPath, route, timeoutMs, enableRuntimeFallback, observationToken = null) {
  const dynamicRoute = route?.id?.startsWith("oc.runtime.route.");
  const observation = dynamicRoute ? pendingRoutes.get(observationToken) : null;
  if (dynamicRoute && (!observation || !observation.dispatched || observation.routeId !== route.id || observation.path !== route.path || observation.title !== route.title))
    return Promise.resolve(result("Uncertain", "route_observation_missing"));
  const matches = () => {
    if (normalizePath(location.pathname) !== normalizePath(expectedPath)) return false;
    if (dynamicRoute) return matchingRouteHeadings(route.title).some((heading) => !observation.previousHeadings.has(heading));
    if (route && route.targets && route.targets.length) {
      return route.targets.some((target) => resolveTarget(target, enableRuntimeFallback, route).status === "resolved");
    }
    return !route || !route.title || document.title === route.title;
  };
  return waitUntil(matches, timeoutMs, "route_ready", "route_not_ready").finally(() => {
    if (observation) { clearTimeout(observation.timer); pendingRoutes.delete(observationToken); }
  });
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

  const visibleMarked = [...scope.querySelectorAll("[" + idAttribute + "]")]
    .filter((element) => element.getAttribute(idAttribute) === target.id && isVisible(element));
  const marked = visibleMarked.filter((element) => isEligibleTarget(element, target));
  if (marked.length > 1) return { status: "ambiguous" };
  if (marked.length === 1) return { status: "resolved", element: marked[0] };
  if (visibleMarked.length > 0) return { status: "missing" };
  if (target.selector) {
    try {
      const visibleSelected = [...scope.querySelectorAll(target.selector)].filter(isVisible);
      const selected = visibleSelected.filter((element) => isEligibleTarget(element, target));
      if (selected.length > 1) return { status: "ambiguous" };
      if (selected.length === 1) return { status: "resolved", element: selected[0] };
      if (visibleSelected.length > 0) return { status: "missing" };
    } catch {
      // Invalid compiler output falls through to semantic resolution.
    }
  }
  if (!enableRuntimeFallback) return { status: "missing" };
  const label = normalize(target.label);
  if (!label) return { status: "missing" };
  const matches = [...scope.querySelectorAll("*")]
    .filter(isVisible)
    .filter((element) => isEligibleTarget(element, target))
    .filter((element) => kindMatches(target.kind, element))
    .filter((element) => (!target.role || roleOf(element) === target.role) &&
      accessibleNames(element).some((name) => normalize(name) === label));
  if (matches.length > 1) return { status: "ambiguous" };
  return matches.length === 1 ? { status: "resolved", element: matches[0] } : { status: "missing" };
}

function isEligibleTarget(element, target) {
  const codeContainer = element.closest("pre, code");
  return !codeContainer || element === codeContainer && target.kind === "Region";
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
  if (tag === "nav") return "navigation";
  if (tag === "main") return "main";
  if (tag === "article") return "article";
  if (tag === "aside") return "complementary";
  if (tag === "section" && (element.getAttribute("aria-label")?.trim() ||
      element.getAttribute("aria-labelledby")?.split(/\s+/).some((id) => document.getElementById(id)?.textContent?.trim()))) return "region";
  if (tag === "ul" || tag === "ol") return "list";
  if (tag === "table") return "table";
  if (tag === "input") {
    if (element.type === "checkbox") return "checkbox";
    if (element.type === "radio") return "radio";
    if (element.type === "search") return "searchbox";
    return "textbox";
  }
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
  if (element.tagName.toLowerCase() === "a") {
    const titles = element.querySelectorAll(":scope > strong");
    if (titles.length === 1 && isVisible(titles[0])) names.push(titles[0].innerText || "");
  }
  if (element.tagName.toLowerCase() === "section") {
    const heading = element.querySelector(":scope > h1, :scope > h2, :scope > h3, :scope > h4, :scope > h5, :scope > h6");
    if (heading && isVisible(heading)) names.push(heading.innerText || "");
  }
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

function isDisabled(element) {
  return element.matches(":disabled") || !!element.closest("[aria-disabled='true']");
}

function setValue(element, value, isCurrentTarget) {
  if (typeof value !== "string" || !("value" in element)) return result("Failed", "value_not_supported");
  if (isDisabled(element)) return result("Denied", "control_disabled");
  if (element.readOnly || element.getAttribute("aria-readonly")?.toLowerCase() === "true") return result("Denied", "control_readonly");
  if (isSensitiveField(element)) {
    return result("Denied", "sensitive_input_blocked");
  }
  const prototype = element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
  if (!setter) return result("Failed", "value_setter_unavailable");
  setter.call(element, value);
  element.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
  if (!isCurrentTarget()) return result("Uncertain", "value_unverified");
  element.dispatchEvent(new Event("change", { bubbles: true }));
  return isCurrentTarget() && element.value === value ? result("Completed", "value_set", true, "value_set") : result("Uncertain", "value_unverified");
}

function selectValue(element, value, isCurrentTarget) {
  if (isSensitiveField(element)) return result("Denied", "sensitive_input_blocked");
  if (!(element instanceof HTMLSelectElement) || typeof value !== "string") return result("Failed", "select_not_supported");
  if (isDisabled(element)) return result("Denied", "select_disabled");
  const options = [...element.options];
  const exactValues = options.filter((item) => item.value === value);
  const matches = exactValues.length ? exactValues : options.filter((item) => normalize(item.label) === normalize(value));
  if (matches.length === 0) return result("MissingTarget", "option_missing");
  if (matches.length > 1) return result("Ambiguous", "option_ambiguous");
  const option = matches[0];
  if (option.disabled || option.parentElement?.matches("optgroup:disabled")) return result("Denied", "option_disabled");
  element.value = option.value;
  element.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
  if (!isCurrentTarget()) return result("Uncertain", "selection_unverified");
  element.dispatchEvent(new Event("change", { bubbles: true }));
  return isCurrentTarget() && element.value === option.value ? result("Completed", "selection_verified", true, "selected") : result("Uncertain", "selection_unverified");
}

function isSensitiveField(element) {
  if (element.hasAttribute("data-sensitive") ||
      ["password", "email", "tel", "number", "hidden", "file"].includes(String(element.type || "").toLowerCase())) {
    return true;
  }

  const autocomplete = element.getAttribute("autocomplete") || "";
  if (sensitiveAutocompletePattern.test(autocomplete)) return true;

  const labelledBy = (element.getAttribute("aria-labelledby") || "").split(/\s+/)
    .map((id) => document.getElementById(id)?.textContent || "").join(" ");
  const labels = element.labels ? [...element.labels].map((label) => label.textContent || "").join(" ") : "";
  const metadata = [element.name, element.id, element.getAttribute("aria-label"), labelledBy,
    element.getAttribute("placeholder"), element.getAttribute("title"), autocomplete, labels].join(" ");
  return sensitiveFieldPattern.test(metadata);
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

function waitForState(target, route, enableRuntimeFallback, name, expected, timeoutMs) {
  const matches = () => {
    if (normalizePath(location.pathname) !== normalizePath(new URL(route.path, document.baseURI).pathname)) return false;
    const resolution = resolveTarget(target, enableRuntimeFallback, route);
    if (resolution.status !== "resolved" || !resolution.element.isConnected) return false;
    const element = resolution.element;
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
  for (const element of highlights) clearHighlight(element);
  highlights.clear();
}

function clearHighlight(element) {
  const timer = highlightTimers.get(element);
  if (timer) clearTimeout(timer);
  highlightTimers.delete(element);
  element.removeAttribute(highlightAttribute);
  highlights.delete(element);
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

function withMeasurements(value, targetResolutionMilliseconds, actionStartedAt) {
  return Promise.resolve(value).then(actionResult => ({
    ...actionResult,
    measurements: {
      targetResolutionMilliseconds,
      browserActionMilliseconds: Math.max(0, performance.now() - actionStartedAt),
    },
  }));
}
