/* composer-drag.js — Handle-driven drag for OpenConnect floating composer.
   Adopted from SwiftAidv2: uses direct viewport pixel coordinates (left, top).
   Guaranteed 1:1 direct tracking with zero reversal or opposing directions. */
(function () {
    const STEP = 24;
    const tracked = new WeakMap();

    function clamp(val, max) {
        return Math.max(0, Math.min(val, Math.max(0, max)));
    }

    function applyPosition(root, x, y) {
        const maxX = window.innerWidth - root.offsetWidth;
        const maxY = window.innerHeight - root.offsetHeight;
        const clampedX = clamp(x, maxX);
        const clampedY = clamp(y, maxY);

        root.style.insetInlineEnd = "auto";
        root.style.insetBlockEnd = "auto";
        root.style.right = "auto";
        root.style.bottom = "auto";
        root.style.left = clampedX + "px";
        root.style.top = clampedY + "px";

        return { x: clampedX, y: clampedY };
    }

    window.ocComposerDrag = {
        attach: function (handle, root, dotnet) {
            if (!handle || !root || tracked.has(handle)) return;

            let dragging = false;
            let startX = 0;
            let startY = 0;
            let initialLeft = 0;
            let initialTop = 0;

            const onPointerDown = function (ev) {
                if (ev.button !== undefined && ev.button !== 0) return;
                const rect = root.getBoundingClientRect();
                dragging = true;
                startX = ev.clientX;
                startY = ev.clientY;
                initialLeft = rect.left;
                initialTop = rect.top;

                root.classList.add("oc-composer--dragging");
                try {
                    handle.setPointerCapture(ev.pointerId);
                } catch (e) {}
                ev.preventDefault();
            };

            const onPointerMove = function (ev) {
                if (!dragging) return;
                const deltaX = ev.clientX - startX;
                const deltaY = ev.clientY - startY;
                const pos = applyPosition(root, initialLeft + deltaX, initialTop + deltaY);
                if (dotnet) {
                    dotnet.invokeMethodAsync("OnInteractPositionChanged", pos.x, pos.y).catch(function () {});
                }
            };

            const onPointerUp = function (ev) {
                if (!dragging) return;
                dragging = false;
                root.classList.remove("oc-composer--dragging");
                try {
                    handle.releasePointerCapture(ev.pointerId);
                } catch (e) {}
            };

            const onKeyDown = function (ev) {
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

                let stepX = 0;
                let stepY = 0;
                switch (ev.key) {
                    case "ArrowLeft": stepX = -STEP; break;
                    case "ArrowRight": stepX = STEP; break;
                    case "ArrowUp": stepY = -STEP; break;
                    case "ArrowDown": stepY = STEP; break;
                    default: return;
                }

                ev.preventDefault();
                const rect = root.getBoundingClientRect();
                applyPosition(root, rect.left + stepX, rect.top + stepY);
            };

            const onResize = function () {
                if (!root.style.left && !root.style.top) return;
                const rect = root.getBoundingClientRect();
                applyPosition(root, rect.left, rect.top);
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
            const rect = root.getBoundingClientRect();
            return applyPosition(root, rect.left + deltaX, rect.top + deltaY);
        },

        detach: function (handle, root, resetPosition) {
            if (!handle || !tracked.has(handle)) return;
            const refs = tracked.get(handle);
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
