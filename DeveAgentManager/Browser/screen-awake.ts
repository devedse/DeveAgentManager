interface WakeState {
    self: DotNetReference;
    enabled: boolean;
    available: boolean;
    lock: WakeLockSentinel | null;
    pending: Promise<void> | null;
    stopped: boolean;
    visibilityChanged: () => void;
    pageHidden: () => void;
    pageShown: () => void;
}
let controller: WakeState | null;

function report(state: WakeState, error: string | null = null) {
    if (!state.stopped)
        state.self.invokeMethodAsync('ScreenAwakeChanged', !!state.lock, error).catch(() => {});
}

async function release(state: WakeState) {
    const lock = state.lock;
    state.lock = null;
    report(state);
    if (lock) await lock.release();
}

function acquire(state: WakeState): Promise<void> {
    if (state.stopped || !state.available || !state.enabled || document.visibilityState !== 'visible' || state.lock)
        return Promise.resolve();
    if (state.pending) return state.pending.then(() => acquire(state));
    state.pending = (async () => {
        try {
            const lock = await navigator.wakeLock.request('screen');
            // A request can finish after the user turns the preference off or closes the app.
            if (state.stopped || !state.enabled || document.visibilityState !== 'visible') {
                await lock.release();
                return;
            }
            state.lock = lock;
            lock.addEventListener('release', () => {
                if (state.lock !== lock) return;
                state.lock = null;
                // Platform revocation pauses the lock without changing the saved preference.
                report(state);
            });
            report(state);
        } catch {
            if (!state.stopped && state.enabled && document.visibilityState === 'visible') {
                report(state, 'Could not keep the screen awake. Check your battery saver and browser settings.');
            }
        }
    })().finally(() => { state.pending = null; });
    return state.pending;
}

export function start(self: DotNetReference, enabled: boolean) {
    const unavailable = !window.isSecureContext ? 'Keep screen awake requires HTTPS'
        : !navigator.wakeLock ? 'Keep screen awake is unavailable in this browser' : null;
    const state: WakeState = controller = { visibilityChanged: null, pageHidden: null, pageShown: null, self, enabled, available: !unavailable, lock: null, pending: null, stopped: false };
    state.visibilityChanged = () => {
        if (document.visibilityState === 'visible') void acquire(state);
        else void release(state).catch(() => {});
    };
    state.pageHidden = () => { void release(state).catch(() => {}); };
    state.pageShown = () => { void acquire(state); };
    document.addEventListener('visibilitychange', state.visibilityChanged);
    window.addEventListener('pagehide', state.pageHidden);
    window.addEventListener('pageshow', state.pageShown);
    report(state);
    void acquire(state);
    return unavailable;
}

export function setEnabled(enabled: boolean) {
    const state = controller;
    if (!state || state.stopped) return;
    if (state.enabled === enabled) { report(state); return; }
    state.enabled = enabled;
    // Saving a preference should not block the UI on an in-flight platform request.
    if (enabled) void acquire(state);
    else void release(state).catch(() => {});
}

export async function stop() {
    const state = controller;
    if (!state) return;
    state.stopped = true;
    state.enabled = false;
    document.removeEventListener('visibilitychange', state.visibilityChanged);
    window.removeEventListener('pagehide', state.pageHidden);
    window.removeEventListener('pageshow', state.pageShown);
    controller = null;
    await release(state);
}
