/* composer-drag.js — handle-only drag for the floating composer bubble.
   Server/ prerender safe: plain script, no imports, no-ops without DOM.
   - Pointer drag starts only on the handle element (handle-only).
   - Arrow keys on the handle nudge 24px; Home resets to the anchored origin.
   - Offsets are clamped so the panel always stays inside the viewport.
   - Position is applied via --oc-dx / --oc-dy custom properties (no layout reflow of host). */
(function () {
    var STEP = 24;
    var tracked = new WeakMap();

    function clampOffsets(panel, dx, dy, curDx, curDy) {
        var rect = panel.getBoundingClientRect();
        var vw = window.innerWidth || document.documentElement.clientWidth || 0;
        var vh = window.innerHeight || document.documentElement.clientHeight || 0;
        var origLeft = rect.left - (curDx || 0);
        var origTop = rect.top - (curDy || 0);

        var minDx = 16 - origLeft;
        var maxDx = vw - 16 - (origLeft + rect.width);
        var minDy = 16 - origTop;
        var maxDy = vh - 16 - (origTop + rect.height);

        if (minDx > maxDx) { minDx = maxDx = 0; }
        if (minDy > maxDy) { minDy = maxDy = 0; }

        return {
            dx: Math.min(maxDx, Math.max(minDx, dx)),
            dy: Math.min(maxDy, Math.max(minDy, dy))
        };
    }

    function apply(panel, state) {
        panel.style.setProperty("--oc-dx", state.dx + "px");
        panel.style.setProperty("--oc-dy", state.dy + "px");
    }

    function reset(panel, state) {
        state.dx = 0;
        state.dy = 0;
        apply(panel, state);
    }

    function onKeyDown(panel, state, ev) {
        var handled = true;
        switch (ev.key) {
            case "ArrowLeft": state.dx -= STEP; break;
            case "ArrowRight": state.dx += STEP; break;
            case "ArrowUp": state.dy -= STEP; break;
            case "ArrowDown": state.dy += STEP; break;
            case "Home": reset(panel, state); break;
            default: handled = false;
        }
        if (handled) {
            ev.preventDefault();
            var clamped = clampOffsets(panel, state.dx, state.dy, state.dx, state.dy);
            state.dx = clamped.dx;
            state.dy = clamped.dy;
            apply(panel, state);
        }
    }

    window.ocComposerDrag = {
        attach: function (handle, panel) {
            if (!handle || !panel || tracked.has(handle)) { return; }
            var state = { dx: 0, dy: 0, dragging: false, startX: 0, startY: 0, baseDx: 0, baseDy: 0 };
            var onPointerDown = function (ev) {
                if (ev.button !== undefined && ev.button !== 0) { return; }
                state.dragging = true;
                state.startX = ev.clientX;
                state.startY = ev.clientY;
                state.baseDx = state.dx;
                state.baseDy = state.dy;
                try { handle.setPointerCapture(ev.pointerId); } catch (e) { /* older input */ }
                ev.preventDefault();
            };
            var onPointerMove = function (ev) {
                if (!state.dragging) { return; }
                var deltaX = ev.clientX - state.startX;
                var deltaY = ev.clientY - state.startY;
                var raw = clampOffsets(panel, state.baseDx + deltaX, state.baseDy + deltaY, state.dx, state.dy);
                state.dx = raw.dx;
                state.dy = raw.dy;
                apply(panel, state);
            };
            var onPointerUp = function () { state.dragging = false; };
            var onKey = function (ev) { onKeyDown(panel, state, ev); };

            handle.addEventListener("pointerdown", onPointerDown);
            window.addEventListener("pointermove", onPointerMove);
            window.addEventListener("pointerup", onPointerUp);
            window.addEventListener("pointercancel", onPointerUp);
            handle.addEventListener("keydown", onKey);
            tracked.set(handle, { onPointerDown: onPointerDown, onPointerMove: onPointerMove, onPointerUp: onPointerUp, onKey: onKey });
        },
        detach: function (handle) {
            if (!handle || !tracked.has(handle)) { return; }
            var refs = tracked.get(handle);
            handle.removeEventListener("pointerdown", refs.onPointerDown);
            window.removeEventListener("pointermove", refs.onPointerMove);
            window.removeEventListener("pointerup", refs.onPointerUp);
            window.removeEventListener("pointercancel", refs.onPointerUp);
            handle.removeEventListener("keydown", refs.onKey);
            tracked.delete(handle);
        }
    };
})();
