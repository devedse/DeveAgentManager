// Reports whether this tab is shown, so live sections can pause their reloads while it is hidden.
let controller: { report: () => void } | null;

export function start(self: DotNetReference) {
    stop();
    const report = () => void self.invokeMethodAsync('Report', document.visibilityState === 'visible').catch(() => {});
    document.addEventListener('visibilitychange', report);
    window.addEventListener('pageshow', report);
    controller = { report };
    return document.visibilityState === 'visible';
}

export function stop() {
    if (!controller) return;
    document.removeEventListener('visibilitychange', controller.report);
    window.removeEventListener('pageshow', controller.report);
    controller = null;
}
