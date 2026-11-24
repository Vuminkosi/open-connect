(function () {
    window.ocVoiceBenchmarkDemo = {
        openAssistant() {
            const launcher = document.querySelector(
                '.oc-mini-composer-fab, [data-oc-composer-launcher], button[aria-label*="OpenConnect Assistant" i], button[aria-label*="Assistant" i]');
            if (launcher && typeof launcher.click === 'function') {
                launcher.click();
                return true;
            }
            return false;
        },
        stopAssistantVoice() {
            if (window.ocComposerVoice && typeof window.ocComposerVoice.stopSession === 'function') {
                window.ocComposerVoice.stopSession();
                return true;
            }
            if (window.ocComposerVoice && typeof window.ocComposerVoice.stop === 'function') {
                window.ocComposerVoice.stop();
                return true;
            }
            return false;
        }
    };
})();
