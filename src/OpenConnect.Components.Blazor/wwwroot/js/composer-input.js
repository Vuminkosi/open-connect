/* composer-input.js — textarea autosize (1–5 lines) + IME-safe Enter bridge.
   Server/prerender safe: plain script, no imports, no-ops without DOM.
   - autosize clamps the editor between 1 and 5 visible lines.
   - attachEnter ignores IME composition (isComposing / keyCode 229) and
     held-key repeats, so Enter submits once and Shift+Enter keeps a newline. */
(function () {
    var LINE_HEIGHT = 21;
    var VERTICAL_PADDING = 16;
    var attached = new WeakMap();

    function lineCap() {
        return (window.matchMedia && window.matchMedia("(max-width: 599px)").matches) ? 5 : 5;
    }

    window.ocComposerInput = {
        autosize: function (el) {
            if (!el || !el.style) { return; }
            var maxLines = lineCap();
            el.style.height = "auto";
            var min = LINE_HEIGHT + VERTICAL_PADDING;
            var max = maxLines * LINE_HEIGHT + VERTICAL_PADDING;
            var next = Math.min(max, Math.max(min, el.scrollHeight));
            el.style.height = next + "px";
            el.style.overflowY = el.scrollHeight > max + 1 ? "auto" : "hidden";
        },
        attachEnter: function (el, dotNet) {
            if (!el || !dotNet || attached.has(el)) { return; }
            var onKey = function (ev) {
                if (ev.key !== "Enter" || ev.shiftKey) { return; }
                // Managed shortcut owns Ctrl/Meta+Enter; Alt+Enter never JS-submits.
                if (ev.ctrlKey || ev.metaKey || ev.altKey) { return; }
                // IME composition or auto-repeat: never submit.
                if (ev.isComposing || ev.keyCode === 229 || ev.repeat) { return; }
                ev.preventDefault();
                try {
                    var p = dotNet.invokeMethodAsync("SubmitFromJs");
                    if (p && typeof p.catch === "function") { p.catch(function () { /* keep typing usable */ }); }
                } catch (e) { /* managed fallback handles Enter */ }
            };
            el.addEventListener("keydown", onKey);
            attached.set(el, onKey);
        },
        detach: function (el) {
            if (!el || !attached.has(el)) { return; }
            try { el.removeEventListener("keydown", attached.get(el)); } catch (e) { /* noop */ }
            attached.delete(el);
        }
    };
})();
