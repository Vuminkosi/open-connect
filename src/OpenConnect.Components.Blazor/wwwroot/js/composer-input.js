/* composer-input.js — textarea autosize (1–5 lines) + IME-safe Enter bridge.
   Server/prerender safe: plain script, no imports, no-ops without DOM.
   - autosize clamps the editor between 1 and 5 visible lines.
   - attachEnter ignores IME composition (isComposing / keyCode 229) and
     held-key repeats, so Enter submits once and Shift+Enter keeps a newline.
   - focus focuses the input element when requested. */
(function () {
    var LINE_HEIGHT = 21;
    var VERTICAL_PADDING = 16;
    var attached = new WeakMap();

    function lineCap() {
        return (window.matchMedia && window.matchMedia("(max-width: 599px)").matches) ? 5 : 5;
    }

    function resolveInput(el) {
        if (!el) return null;
        if (el.tagName === "TEXTAREA" || el.tagName === "INPUT") return el;
        return el.querySelector ? el.querySelector(".oc-bubble__field-wrap textarea, .oc-bubble__field-wrap input, textarea, input") : null;
    }

    window.ocComposerInput = window.ocComposerInput || {};

    window.ocComposerInput.autosize = function (el) {
        var target = resolveInput(el);
        if (!target || !target.style) { return; }
        var maxLines = lineCap();
        target.style.height = "auto";
        var min = LINE_HEIGHT + VERTICAL_PADDING;
        var max = maxLines * LINE_HEIGHT + VERTICAL_PADDING;
        var next = Math.min(max, Math.max(min, target.scrollHeight));
        target.style.height = next + "px";
        target.style.overflowY = target.scrollHeight > max + 1 ? "auto" : "hidden";
    };

    window.ocComposerInput.attachEnter = function (el, dotNet) {
        if (!el || !dotNet || attached.has(el)) { return; }
        var onKey = function (ev) {
            var target = ev.target;
            if (!target || (!target.matches("textarea, input") && target !== el)) { return; }
            if (ev.key !== "Enter" || ev.shiftKey) { return; }
            // Managed shortcut owns Ctrl/Meta+Enter; Alt+Enter never JS-submits.
            if (ev.ctrlKey || ev.metaKey || ev.altKey) { return; }
            // IME composition or auto-repeat: never submit.
            if (ev.isComposing || ev.keyCode === 229 || ev.repeat) { return; }
            ev.preventDefault();
            try {
                var val = target.value || "";
                var p = dotNet.invokeMethodAsync("SubmitKeyboardMessage", val);
                if (p && typeof p.catch === "function") {
                    p.catch(function () {
                        dotNet.invokeMethodAsync("SubmitFromJs");
                    });
                }
            } catch (e) {
                try { dotNet.invokeMethodAsync("SubmitFromJs"); } catch (e2) {}
            }
        };
        el.addEventListener("keydown", onKey);
        attached.set(el, onKey);
    };

    window.ocComposerInput.clear = function (el) {
        var target = resolveInput(el);
        if (!target) { return; }
        target.value = "";
        try {
            target.dispatchEvent(new Event("input", { bubbles: true }));
            target.dispatchEvent(new Event("change", { bubbles: true }));
        } catch (e) { /* noop */ }
    };

    window.ocComposerInput.focus = function (el) {
        var target = resolveInput(el);
        if (target && typeof target.focus === "function") {
            try {
                target.focus();
            } catch (e) {}
        }
    };

    window.ocComposerInput.detach = function (el) {
        if (!el || !attached.has(el)) { return; }
        try { el.removeEventListener("keydown", attached.get(el)); } catch (e) { /* noop */ }
        attached.delete(el);
    };
})();
