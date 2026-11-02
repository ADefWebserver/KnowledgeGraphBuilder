// Knowledge Graph Builder browser helpers. Loaded by both hosts after the Blazor script.
window.kgb = (function () {
    let unsaved = false;
    let shortcutTarget = null;

    window.addEventListener('beforeunload', function (e) {
        if (unsaved) {
            e.preventDefault();
            e.returnValue = '';
        }
    });

    document.addEventListener('keydown', function (e) {
        if (!shortcutTarget) return;
        const key = e.key;
        const ctrl = e.ctrlKey || e.metaKey;
        if (ctrl && (key === 's' || key === 'S')) {
            e.preventDefault();
            shortcutTarget.invokeMethodAsync('OnShortcut', 'save');
        }
    }, true);

    return {
        noop: function () { },

        setUnsaved: function (value) { unsaved = !!value; },

        registerShortcuts: function (target) { shortcutTarget = target; },

        watchWidth: function (target, breakpoint) {
            const mq = window.matchMedia('(min-width: ' + breakpoint + 'px)');
            const handler = function (ev) { target.invokeMethodAsync('OnWidthChanged', ev.matches); };
            if (mq.addEventListener) mq.addEventListener('change', handler); else mq.addListener(handler);
            return mq.matches;
        },

        focus: function (selector) {
            const el = document.querySelector(selector);
            if (el) el.focus();
        },

        scrollToBottom: function (selector) {
            const el = document.querySelector(selector);
            if (el) el.scrollTop = el.scrollHeight;
        },

        // Browser host only: forward localStorage changes made by other tabs (D18).
        registerStorageListener: function (target, prefix) {
            window.addEventListener('storage', function (e) {
                if (e.storageArea !== window.localStorage) return;
                if (e.key === null || e.key.startsWith(prefix)) {
                    target.invokeMethodAsync('OnStorageChanged', e.key);
                }
            });
        },

        storageGet: function (key) { return window.localStorage.getItem(key); },

        saveAsFile: async function (fileName, streamRef) {
            const buffer = await streamRef.arrayBuffer();
            const blob = new Blob([buffer], { type: 'application/zip' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        }
    };
})();

// Chat composer: Enter sends, Shift+Enter adds a new line, Esc stops a stream.
window.kgb.bindComposer = function (id, target) {
    const el = document.getElementById(id);
    if (!el || el.dataset.kgbBound) return;
    el.dataset.kgbBound = '1';
    el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            target.invokeMethodAsync('OnComposerEnter', el.value);
        } else if (e.key === 'Escape') {
            target.invokeMethodAsync('OnComposerEscape');
        }
    });
};
window.kgb.clearValue = function (id) {
    const el = document.getElementById(id);
    if (el) { el.value = ''; el.dispatchEvent(new Event('input', { bubbles: true })); }
};
