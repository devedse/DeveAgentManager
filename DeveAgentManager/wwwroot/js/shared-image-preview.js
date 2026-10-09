const previews = new WeakMap();

export function start(host, self) {
    stop(host);
    const dialog = host.closest('.mud-dialog');
    const onKey = event => {
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.isComposing) return;
        if (event.target instanceof Element && event.target.closest('input, textarea, select, [contenteditable="true"], [role="textbox"]')) return;
        const dialogs = [...document.querySelectorAll('.mud-dialog')].filter(el => el.getClientRects().length);
        if (!host.isConnected || dialogs.at(-1) !== dialog) return;
        event.preventDefault();
        event.stopPropagation();
        self.invokeMethodAsync('Navigate', event.key === 'ArrowLeft' ? -1 : 1).catch(() => {});
    };
    document.addEventListener('keydown', onKey, true);
    previews.set(host, onKey);
}

export function stop(host) {
    const onKey = previews.get(host);
    if (onKey) document.removeEventListener('keydown', onKey, true);
    previews.delete(host);
}
