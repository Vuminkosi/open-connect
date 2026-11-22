(function () {
    const key = 'openconnect-theme-dark';
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const subscribers = new Map();
    let nextId = 0;
    let explicit = null;
    try {
        const stored = localStorage.getItem(key);
        if (stored === 'true' || stored === 'false') explicit = stored === 'true';
    } catch { /* The system preference remains available without storage. */ }
    let dark = explicit ?? media.matches;
    function apply() {
        document.documentElement.dataset.theme = dark ? 'dark' : 'light';
        for (const subscriber of subscribers.values())
            subscriber.invokeMethodAsync('OnThemeChanged', dark).catch(() => {});
    }
    apply();
    media.addEventListener('change', event => { if (explicit === null) { dark = event.matches; apply(); } });
    window.addEventListener('storage', event => {
        if (event.key !== key) return;
        explicit = event.newValue === 'true' ? true : event.newValue === 'false' ? false : null;
        dark = explicit ?? media.matches;
        apply();
    });
    window.ocSiteTheme = {
        subscribe(reference) { const id = ++nextId; subscribers.set(id, reference); return { id, isDark: dark }; },
        unsubscribe(id) { subscribers.delete(id); },
        toggle() {
            explicit = !dark;
            dark = explicit;
            try { localStorage.setItem(key, String(dark)); } catch { /* Keep the choice for this page. */ }
            apply();
            return dark;
        }
    };
})();
