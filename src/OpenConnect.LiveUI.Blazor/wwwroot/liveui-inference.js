let modelSession;
let activeController;
let modelAvailabilityOptions = {};
const modelSetupBindings = new WeakMap();

function providerName() {
    const vendor = navigator.userAgentData?.brands?.map(item => item.brand.toLowerCase()).join(" ") ?? "";
    return vendor.includes("edge") || /edg\//i.test(navigator.userAgent) ? "edge" :
        vendor.includes("chrome") || /chrome\//i.test(navigator.userAgent) ? "chrome" : "unknown";
}

function availabilityName(value) {
    if (value === "available") return "Available";
    if (value === "downloadable") return "Downloadable";
    if (value === "downloading") return "Downloading";
    return "Unavailable";
}

function permissionsAllowModel() {
    const policy = document.permissionsPolicy ?? document.featurePolicy;
    if (!policy?.allowsFeature) return true;
    try { return policy.allowsFeature("language-model"); }
    catch { return true; }
}

function providerAllowed(preference) {
    if (!preference || preference === "Auto") return true;
    const browser = providerName();
    return browser === preference.toLowerCase();
}

export async function getAvailability(preferredProvider = "Auto") {
    if (!globalThis.isSecureContext) return { provider: "Browser", status: "Blocked", browser: providerName(), reason: "secure_context_required" };
    if (!permissionsAllowModel()) return { provider: "Browser", status: "Blocked", browser: providerName(), reason: "permissions_policy_blocked" };
    if (!providerAllowed(preferredProvider)) return { provider: "Browser", status: "Unavailable", browser: providerName(), reason: "browser_provider_mismatch" };
    if (!globalThis.LanguageModel?.availability) return { provider: "Browser", status: "Unavailable", browser: providerName(), reason: "prompt_api_unavailable" };
    try {
        const status = await LanguageModel.availability(modelAvailabilityOptions);
        return { provider: "Browser", status: availabilityName(status), browser: providerName(),
            supportsStructuredOutput: true, supportsCancellation: true, reason: null };
    } catch {
        return { provider: "Browser", status: "Blocked", browser: providerName(), reason: "availability_check_failed" };
    }
}

export async function initialize(preferredProvider = "Auto") {
    if (!permissionsAllowModel()) return { provider: "Browser", status: "Blocked", browser: providerName(), reason: "permissions_policy_blocked" };
    if (!providerAllowed(preferredProvider)) return { provider: "Browser", status: "Unavailable", browser: providerName(), reason: "browser_provider_mismatch" };
    if (!globalThis.LanguageModel?.create) return { provider: "Browser", status: "Unavailable", browser: providerName(), reason: "prompt_api_unavailable" };
    try {
        const status = await LanguageModel.availability(modelAvailabilityOptions);
        if (status === "unavailable") return { provider: "Browser", status: "Unavailable", browser: providerName(), reason: "model_unavailable" };
        if (status === "downloading") return { provider: "Browser", status: "Downloading", browser: providerName(), reason: "model_downloading" };
        if (status === "downloadable" && !navigator.userActivation?.isActive)
            return { provider: "Browser", status: "Downloadable", browser: providerName(), reason: "user_activation_required" };
        if (modelSession) return { provider: "Browser", status: "Available", browser: providerName(), supportsStructuredOutput: true, supportsCancellation: true, reason: null };
        modelSession?.destroy?.();
        modelSession = await LanguageModel.create({ ...modelAvailabilityOptions, monitor(monitor) {
            monitor.addEventListener("downloadprogress", event => globalThis.dispatchEvent(new CustomEvent("openconnect-liveui-model-progress", { detail: { loaded: event.loaded, total: event.total } })));
        }});
        const availability = await getAvailability(preferredProvider);
        return availability;
    } catch {
        return { provider: "Browser", status: "Unavailable", browser: providerName(), reason: "model_initialization_failed" };
    }
}

function initializeFromGesture(preferredProvider = "Auto") {
    if (!globalThis.isSecureContext) return Promise.resolve({ provider: "Browser", status: "Blocked", browser: providerName(), reason: "secure_context_required" });
    if (!permissionsAllowModel()) return Promise.resolve({ provider: "Browser", status: "Blocked", browser: providerName(), reason: "permissions_policy_blocked" });
    if (!providerAllowed(preferredProvider)) return Promise.resolve({ provider: "Browser", status: "Unavailable", browser: providerName(), reason: "browser_provider_mismatch" });
    if (!globalThis.LanguageModel?.create) return Promise.resolve({ provider: "Browser", status: "Unavailable", browser: providerName(), reason: "prompt_api_unavailable" });
    if (modelSession) return getAvailability(preferredProvider);

    // Call create synchronously within the native click handler. Waiting for a Blazor Server
    // event round trip first would lose the transient user activation required for downloads.
    let creation;
    try {
        creation = LanguageModel.create({ ...modelAvailabilityOptions, monitor(monitor) {
            monitor.addEventListener("downloadprogress", event => globalThis.dispatchEvent(new CustomEvent("openconnect-liveui-model-progress", { detail: { loaded: event.loaded, total: event.total } })));
        }});
    } catch {
        return Promise.resolve({ provider: "Browser", status: "Unavailable", browser: providerName(), reason: "model_initialization_failed" });
    }
    return Promise.resolve(creation).then(async session => {
        modelSession?.destroy?.();
        modelSession = session;
        return getAvailability(preferredProvider);
    }).catch(() => ({ provider: "Browser", status: "Unavailable", browser: providerName(), reason: "model_initialization_failed" }));
}

export function bindUserGestureInitializer(button, dotNetReference, preferredProvider = "Auto") {
    if (!button || modelSetupBindings.has(button)) return;
    const onProgress = event => {
        void dotNetReference.invokeMethodAsync("OnModelDownloadProgress", event.detail.loaded, event.detail.total).catch(() => {});
    };
    const onClick = () => {
        globalThis.addEventListener("openconnect-liveui-model-progress", onProgress);
        const initialization = initializeFromGesture(preferredProvider);
        button.disabled = true;
        button.textContent = "Preparing on-device model…";
        void dotNetReference.invokeMethodAsync("OnModelInitializationStarted").catch(() => {});
        void initialization.then(result => {
            unbindUserGestureInitializer(button);
            return dotNetReference.invokeMethodAsync("OnModelInitializationCompleted", result);
        }).catch(() => {
            unbindUserGestureInitializer(button);
            return dotNetReference.invokeMethodAsync("OnModelInitializationCompleted", {
                provider: "Browser", status: "Unavailable", browser: providerName(), reason: "model_initialization_failed"
            });
        }).finally(() => globalThis.removeEventListener("openconnect-liveui-model-progress", onProgress));
    };
    button.addEventListener("click", onClick);
    modelSetupBindings.set(button, onClick);
}

export function unbindUserGestureInitializer(button) {
    const handler = button && modelSetupBindings.get(button);
    if (!handler) return;
    button.removeEventListener("click", handler);
    modelSetupBindings.delete(button);
}

export async function generate(request, enableStructuredOutput = true) {
    if (!modelSession) throw new Error("model_not_initialized");
    if (activeController) throw new Error("inference_in_progress");
    activeController = new AbortController();
    const prompt = `Return a JSON object with schemaVersion and actions. Use only the supplied target IDs and action capabilities. Do not invent targets.\nRequest: ${request.intent}\nRoute: ${request.routeId}\nSchema: ${request.schemaVersion}\nCapabilities: ${JSON.stringify(request.capabilities)}`;
    const promptOptions = { signal: activeController.signal };
    if (enableStructuredOutput) promptOptions.responseConstraint = { type: "object", required: ["schemaVersion", "actions"], additionalProperties: false,
            properties: { schemaVersion: { type: "string" }, actions: { type: "array", items: { type: "object", required: ["type", "targetId"], additionalProperties: false,
                properties: { type: { type: "string" }, targetId: { type: "string" }, value: { type: "string" } } } } } };
    try {
        const response = await modelSession.prompt(prompt, promptOptions);
        const value = typeof response === "string" ? JSON.parse(response) : response;
        if (!value || typeof value !== "object" || !Array.isArray(value.actions)) throw new Error("invalid_plan_shape");
        return value;
    } finally {
        activeController = undefined;
    }
}

export function cancel() { activeController?.abort(); }
export function dispose() { activeController?.abort(); activeController = undefined; modelSession?.destroy?.(); modelSession = undefined; }
globalThis.addEventListener("pagehide", dispose, { once: true });
