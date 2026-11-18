/* composer-drag.js — Handle-driven drag for OpenConnect floating composer.
   Adopted from SwiftAidv2: uses direct viewport pixel coordinates (left, top).
   Guaranteed 1:1 direct tracking with zero reversal or opposing directions. */
(function () {
    var STEP = 24;
    var tracked = new WeakMap();

    function clamp(val, max) {
        return Math.max(0, Math.min(val, Math.max(0, max)));
    }

    function getOffsetParentRect(el) {
        var p = el ? el.offsetParent : null;
        if (!p || p === document.body || p === document.documentElement) {
            return { left: 0, top: 0 };
        }
        return p.getBoundingClientRect();
    }

    function applyPosition(root, x, y) {
        var parentRect = getOffsetParentRect(root);
        var maxX = window.innerWidth - root.offsetWidth;
        var maxY = window.innerHeight - root.offsetHeight;

        var clampedViewportX = clamp(x + parentRect.left, maxX);
        var clampedViewportY = clamp(y + parentRect.top, maxY);

        var finalX = clampedViewportX - parentRect.left;
        var finalY = clampedViewportY - parentRect.top;

        root.style.setProperty("inset-inline-end", "auto", "important");
        root.style.setProperty("inset-block-end", "auto", "important");
        root.style.setProperty("right", "auto", "important");
        root.style.setProperty("bottom", "auto", "important");
        root.style.setProperty("left", finalX + "px", "important");
        root.style.setProperty("top", finalY + "px", "important");

        return { x: clampedViewportX, y: clampedViewportY };
    }

    window.ocComposerDrag = {
        attach: function (handle, root, dotnet) {
            if (!handle || !root || tracked.has(handle)) return;

            var dragging = false;
            var startX = 0;
            var startY = 0;
            var initialLeft = 0;
            var initialTop = 0;

            var onPointerDown = function (ev) {
                if (ev.button !== undefined && ev.button !== 0) return;
                var parentRect = getOffsetParentRect(root);
                var rect = root.getBoundingClientRect();
                dragging = true;
                startX = ev.clientX;
                startY = ev.clientY;
                initialLeft = rect.left - parentRect.left;
                initialTop = rect.top - parentRect.top;

                root.classList.add("oc-composer--dragging");
                try {
                    handle.setPointerCapture(ev.pointerId);
                } catch (e) {}
                ev.preventDefault();
            };

            var onPointerMove = function (ev) {
                if (!dragging) return;
                var deltaX = ev.clientX - startX;
                var deltaY = ev.clientY - startY;
                var pos = applyPosition(root, initialLeft + deltaX, initialTop + deltaY);
                if (dotnet) {
                    try { dotnet.invokeMethodAsync("OnInteractPositionChanged", pos.x, pos.y); } catch (e) {}
                }
            };

            var onPointerUp = function (ev) {
                if (!dragging) return;
                dragging = false;
                root.classList.remove("oc-composer--dragging");
                try {
                    if (ev && ev.pointerId !== undefined && handle.hasPointerCapture && handle.hasPointerCapture(ev.pointerId)) {
                        handle.releasePointerCapture(ev.pointerId);
                    }
                } catch (e) {}
            };

            var onKeyDown = function (ev) {
                if (ev.key === "Home") {
                    root.style.removeProperty("left");
                    root.style.removeProperty("top");
                    root.style.removeProperty("right");
                    root.style.removeProperty("bottom");
                    root.style.removeProperty("inset-inline-end");
                    root.style.removeProperty("inset-block-end");
                    ev.preventDefault();
                    return;
                }

                var stepX = 0;
                var stepY = 0;
                switch (ev.key) {
                    case "ArrowLeft": stepX = -STEP; break;
                    case "ArrowRight": stepX = STEP; break;
                    case "ArrowUp": stepY = -STEP; break;
                    case "ArrowDown": stepY = STEP; break;
                    default: return;
                }

                ev.preventDefault();
                var parentRect = getOffsetParentRect(root);
                var rect = root.getBoundingClientRect();
                applyPosition(root, (rect.left - parentRect.left) + stepX, (rect.top - parentRect.top) + stepY);
            };

            var onResize = function () {
                if (!root.style.left && !root.style.top) return;
                var parentRect = getOffsetParentRect(root);
                var rect = root.getBoundingClientRect();
                applyPosition(root, rect.left - parentRect.left, rect.top - parentRect.top);
            };

            handle.addEventListener("pointerdown", onPointerDown);
            window.addEventListener("pointermove", onPointerMove);
            window.addEventListener("pointerup", onPointerUp);
            window.addEventListener("pointercancel", onPointerUp);
            handle.addEventListener("keydown", onKeyDown);
            window.addEventListener("resize", onResize, { passive: true });

            tracked.set(handle, {
                onPointerDown: onPointerDown,
                onPointerMove: onPointerMove,
                onPointerUp: onPointerUp,
                onKeyDown: onKeyDown,
                onResize: onResize
            });
        },

        moveBy: function (root, deltaX, deltaY) {
            if (!root) return { x: 0, y: 0 };
            var parentRect = getOffsetParentRect(root);
            var rect = root.getBoundingClientRect();
            return applyPosition(root, (rect.left - parentRect.left) + deltaX, (rect.top - parentRect.top) + deltaY);
        },

        detach: function (handle, root, resetPosition) {
            if (!handle || !tracked.has(handle)) return;
            var refs = tracked.get(handle);
            handle.removeEventListener("pointerdown", refs.onPointerDown);
            window.removeEventListener("pointermove", refs.onPointerMove);
            window.removeEventListener("pointerup", refs.onPointerUp);
            window.removeEventListener("pointercancel", refs.onPointerUp);
            handle.removeEventListener("keydown", refs.onKeyDown);
            window.removeEventListener("resize", refs.onResize);
            tracked.delete(handle);

            if (root) {
                root.classList.remove("oc-composer--dragging");
                if (resetPosition) {
                    root.style.removeProperty("left");
                    root.style.removeProperty("top");
                    root.style.removeProperty("right");
                    root.style.removeProperty("bottom");
                    root.style.removeProperty("inset-inline-end");
                    root.style.removeProperty("inset-block-end");
                }
            }
        }
    };
})();
