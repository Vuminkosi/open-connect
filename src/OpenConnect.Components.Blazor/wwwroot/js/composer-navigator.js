/* Safe client-side navigator actions emitted by authorized SDK tools. */
(function () {
    function target(id) {
        if (!id || !/^[a-zA-Z0-9_-]{1,80}$/.test(id)) return null;
        return document.querySelector('[data-oc-target="' + id + '"], #' + CSS.escape(id));
    }
    function apply(action, route, targetId) {
        const node = target(targetId);
        if (action === 'navigate' && typeof route === 'string' && route.startsWith('/') && !route.startsWith('//')) {
            window.history.pushState({}, '', route);
            window.dispatchEvent(new PopStateEvent('popstate'));
        } else if (action === 'highlight' && node) {
            document.querySelectorAll('.oc-navigator-highlight').forEach(x => x.classList.remove('oc-navigator-highlight'));
            node.classList.add('oc-navigator-highlight');
            node.scrollIntoView({ behavior: 'smooth', block: 'center' });
        } else if (action === 'focus' && node) {
            node.focus?.();
            node.scrollIntoView({ behavior: 'smooth', block: 'center' });
        } else if (action === 'scroll' && node) {
            node.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }
    window.ocComposerNavigator = { apply };
})();
