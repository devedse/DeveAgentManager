// xterm's existing OSC 8 and web-links providers find URLs. Handle activation on this device,
// and consume link gestures before terminal mouse reporting can send them to the Runner.
window.damTerminalLinks = {
    install(terminal) {
        const element = terminal.element;
        const screen = element?.querySelector('.xterm-screen');
        if (!screen) return { dispose() {} };
        const events = new AbortController();
        const subscriptions = [];
        let active = null, press = null, touch = null;
        const safeUrl = uri => {
            try {
                const url = new URL(uri);
                return ['http:', 'https:'].includes(url.protocol) ? url.href : null;
            } catch { return null; }
        };
        const open = (event, uri) => {
            const url = safeUrl(uri);
            if (!url) return;
            event.preventDefault();
            event.stopPropagation();
            window.open(url, '_blank', 'noopener,noreferrer');
        };
        const handler = {
            activate: open,
            hover: (event, uri, range) => { active = { uri, range, x: event.clientX, y: event.clientY }; screen.title = safeUrl(uri) ?? ''; },
            leave: () => { active = null; screen.removeAttribute('title'); },
            allowNonHttpProtocols: false,
        };
        terminal.options.linkHandler = handler;
        const addon = new WebLinksAddon.WebLinksAddon(open, handler);
        terminal.loadAddon(addon);
        const listen = (target, name, callback, options = {}) => target.addEventListener(name, callback, { ...options, signal: events.signal });
        const clear = () => { active = null; press = null; screen.removeAttribute('title'); };
        subscriptions.push(terminal.onScroll(clear), terminal.onResize(clear), terminal.onWriteParsed(clear));
        const at = point => {
            if (!active || !safeUrl(active.uri)) return null;
            const box = screen.getBoundingClientRect();
            const x = Math.floor((point.clientX - box.left) * terminal.cols / box.width) + 1;
            const y = Math.floor((point.clientY - box.top) * terminal.rows / box.height) + 1 + terminal.buffer.active.viewportY;
            if (x < 1 || x > terminal.cols || point.clientY < box.top || point.clientY >= box.bottom) return null;
            if (active.range) {
                const { start, end } = active.range;
                return (y > start.y || y === start.y && x >= start.x) && (y < end.y || y === end.y && x <= end.x) ? active.uri : null;
            }
            // Older OSC 8 callbacks supply no range; only the exact hovered cell may activate.
            const cell = value => Math.floor(value * terminal.cols / box.width);
            return cell(point.clientX - box.left) === cell(active.x - box.left)
                && Math.abs(point.clientY - active.y) < box.height / terminal.rows ? active.uri : null;
        };
        // This bubble listener runs after xterm's linkifier on the screen and before mouse reporting
        // on its parent. Synthetic moves used to resolve a touch must never reach the remote TUI.
        listen(screen, 'mousemove', event => { if (!event.isTrusted && touch) event.stopPropagation(); });
        listen(screen, 'mousedown', event => {
            if (event.button !== 0 || event.shiftKey) return;
            press = at(event);
            if (press) { event.preventDefault(); event.stopImmediatePropagation(); }
        }, { capture: true });
        listen(screen, 'mouseup', event => {
            if (!press) return;
            const uri = press;
            press = null;
            event.preventDefault(); event.stopImmediatePropagation();
            if (uri === at(event)) open(event, uri);
        }, { capture: true });
        listen(element, 'touchstart', event => {
            if (event.touches.length !== 1) { touch = null; return; }
            const point = event.touches[0];
            touch = { x: point.clientX, y: point.clientY, at: performance.now(), moved: false };
            // A finger has no hover. Ask xterm's own providers to resolve its cell before release.
            screen.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: point.clientX, clientY: point.clientY }));
        }, { passive: true, capture: true });
        listen(element, 'touchmove', event => {
            if (touch && (event.touches.length !== 1 || Math.hypot(event.touches[0].clientX - touch.x, event.touches[0].clientY - touch.y) > 8)) touch.moved = true;
        }, { passive: true, capture: true });
        listen(element, 'touchcancel', () => { touch = null; });
        listen(element, 'touchend', event => {
            const point = event.changedTouches[0];
            const uri = touch && !touch.moved && performance.now() - touch.at < 600 && point ? at(point) : null;
            touch = null;
            if (uri) {
                event.preventDefault(); event.stopImmediatePropagation();
                open(event, uri);
            }
        }, { passive: false, capture: true });
        return { dispose() { events.abort(); subscriptions.forEach(s => s.dispose()); addon.dispose(); clear(); } };
    },
};
