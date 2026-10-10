let controller;

export function start(self) {
    stop();
    const check = () => {
        if (document.visibilityState === 'visible') void self.invokeMethodAsync('CheckForUpdate').catch(() => {});
    };
    document.addEventListener('visibilitychange', check);
    window.addEventListener('pageshow', check);
    const timer = setInterval(check, 60000);
    controller = { check, timer };
}

export function stop() {
    if (!controller) return;
    document.removeEventListener('visibilitychange', controller.check);
    window.removeEventListener('pageshow', controller.check);
    clearInterval(controller.timer);
    controller = null;
}
