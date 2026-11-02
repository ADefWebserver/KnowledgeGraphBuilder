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

// Explorer drag and drop with pointer events. HTML5 drag and drop doesn't work inside MAUI's WebView2 on Windows
// (the drag ends as soon as it starts), so both hosts use this. Items carry data-kgb-path; folders and the root
// drop zone also carry data-kgb-drop. The target sent to .NET is a folder path, "" for the root, or null for none.
window.kgb.bindTreeDrag = function (root, target) {
    if (!root || root.dataset.kgbDrag) return;
    root.dataset.kgbDrag = '1';
    let start = null, dragging = false, over;

    function dropTargetAt(x, y) {
        const el = document.elementFromPoint(x, y);
        const hit = el && root.contains(el) ? el.closest('[data-kgb-drop]') : null;
        return hit ? hit.dataset.kgbPath : null;
    }
    function finish(drop) {
        document.removeEventListener('pointermove', onMove, true);
        document.removeEventListener('pointerup', onUp, true);
        document.removeEventListener('keydown', onKey, true);
        if (dragging) {
            target.invokeMethodAsync('OnPointerDrop', drop ? over : null);
            // The pointerup ends with a click on the item under it; don't let that open or select it.
            const swallow = function (e) { e.stopPropagation(); e.preventDefault(); };
            document.addEventListener('click', swallow, { capture: true, once: true });
            setTimeout(function () { document.removeEventListener('click', swallow, true); }, 0);
        }
        start = null; dragging = false; over = undefined;
    }
    function onMove(e) {
        if (!start) return;
        if (!dragging) {
            if (Math.abs(e.clientX - start.x) + Math.abs(e.clientY - start.y) < 6) return;
            dragging = true;
            target.invokeMethodAsync('OnPointerDragStart', start.path);
        }
        const next = dropTargetAt(e.clientX, e.clientY);
        if (next !== over) {
            over = next;
            target.invokeMethodAsync('OnPointerDragOver', next);
        }
    }
    function onUp() { finish(true); }
    function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); finish(false); } }

    // F2 and Delete. RadzenTree stops keydown propagation inside Blazor, so a Blazor @onkeydown on an ancestor never runs.
    root.addEventListener('keydown', function (e) {
        if ((e.key === 'F2' || e.key === 'Delete') && !e.target.closest('input, textarea, [contenteditable]')) {
            e.preventDefault();
            target.invokeMethodAsync('OnTreeKey', e.key);
        }
    });

    root.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        const item = e.target.closest('[data-kgb-path]');
        if (!item || !root.contains(item) || item.dataset.kgbPath === '') return;
        start = { x: e.clientX, y: e.clientY, path: item.dataset.kgbPath };
        document.addEventListener('pointermove', onMove, true);
        document.addEventListener('pointerup', onUp, true);
        document.addEventListener('keydown', onKey, true);
    });
};

// Graph page: wheel zoom around the pointer, drag to pan (both by changing the SVG viewBox), and hover highlighting of
// a node's links and neighbors. Blazor re-renders the SVG's children, so state lives on the element, and a rebuilt
// layout (new viewBox attribute from .NET) resets the view.
window.kgb.bindGraph = function (svg) {
    if (!svg) return;
    const home = svg.getAttribute('viewBox');
    if (svg.kgbGraph) {
        if (svg.kgbGraph.home !== home) { svg.kgbGraph.home = home; svg.kgbGraph.view = parse(home); }
        return;
    }
    function parse(v) { const p = v.split(/\s+/).map(Number); return { x: p[0], y: p[1], w: p[2], h: p[3] }; }
    const state = svg.kgbGraph = { home: home, view: parse(home), drag: null, moved: false };
    function apply() { const v = state.view; svg.setAttribute('viewBox', v.x + ' ' + v.y + ' ' + v.w + ' ' + v.h); }
    function toGraph(e) {
        const r = svg.getBoundingClientRect(), v = state.view;
        // preserveAspectRatio "xMidYMid meet": one scale for both axes, centered.
        const s = Math.min(r.width / v.w, r.height / v.h);
        return { x: v.x + (e.clientX - r.left - (r.width - v.w * s) / 2) / s, y: v.y + (e.clientY - r.top - (r.height - v.h * s) / 2) / s, s: s };
    }

    svg.addEventListener('wheel', function (e) {
        e.preventDefault();
        const p = toGraph(e), v = state.view, home = parse(state.home);
        const factor = Math.exp(Math.sign(e.deltaY) * 0.15);
        const w = Math.min(home.w * 4, Math.max(home.w / 20, v.w * factor));
        const k = w / v.w;
        state.view = { x: p.x - (p.x - v.x) * k, y: p.y - (p.y - v.y) * k, w: w, h: v.h * k };
        apply();
    }, { passive: false });

    svg.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        state.drag = { x: e.clientX, y: e.clientY, view: Object.assign({}, state.view), s: toGraph(e).s };
        state.moved = false;
    });
    window.addEventListener('pointermove', function (e) {
        const d = state.drag;
        if (!d) return;
        const mx = e.clientX - d.x, my = e.clientY - d.y;
        if (!state.moved && Math.abs(mx) + Math.abs(my) < 4) return;
        if (!state.moved) { state.moved = true; svg.classList.add('panning'); }
        state.view = { x: d.view.x - mx / d.s, y: d.view.y - my / d.s, w: d.view.w, h: d.view.h };
        apply();
    });
    window.addEventListener('pointerup', function () {
        if (!state.drag) return;
        state.drag = null;
        svg.classList.remove('panning');
    });
    // A pan ends with a click on whatever is under the pointer; don't let it select a node.
    svg.addEventListener('click', function (e) {
        if (state.moved) { e.stopPropagation(); e.preventDefault(); state.moved = false; }
    }, true);

    function highlight(id) {
        svg.classList.toggle('hovering', !!id);
        svg.querySelectorAll('.hl').forEach(function (el) { el.classList.remove('hl'); });
        if (!id) return;
        const near = new Set([id]);
        svg.querySelectorAll('.kgb-gedge').forEach(function (l) {
            if (l.dataset.from === id || l.dataset.to === id) { l.classList.add('hl'); near.add(l.dataset.from); near.add(l.dataset.to); }
        });
        svg.querySelectorAll('.kgb-gnode').forEach(function (g) { if (near.has(g.dataset.id)) g.classList.add('hl'); });
    }
    svg.addEventListener('pointerover', function (e) {
        if (state.drag) return;
        const g = e.target.closest('.kgb-gnode');
        highlight(g ? g.dataset.id : null);
    });
    svg.addEventListener('pointerleave', function () { highlight(null); });
};
window.kgb.fitGraph = function (svg) {
    if (!svg || !svg.kgbGraph) return;
    const p = svg.kgbGraph.home.split(/\s+/).map(Number);
    svg.kgbGraph.view = { x: p[0], y: p[1], w: p[2], h: p[3] };
    svg.setAttribute('viewBox', svg.kgbGraph.home);
};

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
