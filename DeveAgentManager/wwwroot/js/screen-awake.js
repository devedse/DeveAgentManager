const controllers = new WeakMap();

function report(state, error = null) {
    if (!state.stopped)
        state.self.invokeMethodAsync('ScreenAwakeChanged', state.enabled, !!state.lock, error).catch(() => {});
}

async function release(state) {
    const lock = state.lock;
    state.lock = null;
    report(state);
    if (lock) await lock.release();
}

function acquire(state) {
    if (state.stopped || !state.enabled || document.visibilityState !== 'visible' || state.lock)
        return Promise.resolve();
    if (state.pending) return state.pending.then(() => acquire(state));
    state.pending = (async () => {
        try {
            const lock = await navigator.wakeLock.request('screen');
            // A request can finish after the user turns the toggle off or leaves the terminal.
            if (state.stopped || !state.enabled || document.visibilityState !== 'visible') {
                await lock.release();
                return;
            }
            state.lock = lock;
            lock.addEventListener('release', () => {
                if (state.lock !== lock) return;
                state.lock = null;
                // Keep the preference when backgrounded; a visible system revocation turns it off.
                if (document.visibilityState === 'visible') state.enabled = false;
                report(state);
            });
            report(state);
        } catch {
            if (!state.stopped && state.enabled && document.visibilityState === 'visible') {
                state.enabled = false;
                report(state, 'Could not keep the screen awake. Check your battery saver and browser settings.');
            }
        }
    })().finally(() => { state.pending = null; });
    return state.pending;
}

export function start(host, self) {
    if (!window.isSecureContext) return 'Keep screen awake requires HTTPS';
    if (!navigator.wakeLock) return 'Keep screen awake is unavailable in this browser';
    const state = { self, enabled: false, lock: null, pending: null, stopped: false };
    state.visibilityChanged = () => {
        if (document.visibilityState === 'visible') void acquire(state);
        else void release(state).catch(() => {});
    };
    controllers.set(host, state);
    document.addEventListener('visibilitychange', state.visibilityChanged);
    return null;
}

export async function toggle(host) {
    const state = controllers.get(host);
    if (!state || state.stopped) return;
    state.enabled = !state.enabled;
    if (state.enabled) await acquire(state);
    else await release(state);
}

export async function stop(host) {
    const state = controllers.get(host);
    if (!state) return;
    state.stopped = true;
    state.enabled = false;
    document.removeEventListener('visibilitychange', state.visibilityChanged);
    controllers.delete(host);
    await release(state);
}
