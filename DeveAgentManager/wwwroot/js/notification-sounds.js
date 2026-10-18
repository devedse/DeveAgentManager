// CC0 Kenney Interface Sounds. Opt-in, per user/browser, never replay a page's initial statuses.
window.damNotificationSounds = (() => {
    let user, prefs = { finished: false, limit: false }, previous = null, context;
    const buffers = new Map();
    const settingsKey = () => `dam.notification-sounds.${user}`;
    const read = (key, fallback) => { try { return JSON.parse(localStorage.getItem(key)) ?? fallback; } catch { return fallback; } };
    const write = (key, value) => { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* Private browsing may disallow storage. */ } };
    async function unlock() {
        if (!prefs.finished && !prefs.limit) return;
        try {
            context ??= new (window.AudioContext || window.webkitAudioContext)();
            await context.resume();
        } catch { /* An unavailable audio device must not affect notifications. */ }
    }
    async function play(kind) {
        try {
            if (!context || context.state !== 'running') return;
            if (!buffers.has(kind)) {
                const response = await fetch(new URL(`audio/${kind === 'limit' ? 'usage-limit' : 'finished'}.wav`, document.baseURI));
                if (!response.ok) return;
                buffers.set(kind, await context.decodeAudioData(await response.arrayBuffer()));
            }
            const source = context.createBufferSource(), gain = context.createGain();
            source.buffer = buffers.get(kind);
            gain.gain.value = 0.25;
            source.connect(gain).connect(context.destination);
            source.start();
        } catch { /* Autoplay policies or an interrupted device can silence audio. */ }
    }
    document.addEventListener('pointerdown', unlock, { passive: true });
    document.addEventListener('keydown', unlock);
    return {
        init(id) {
            if (user !== id) { user = id; previous = null; }
            const saved = read(settingsKey(), {});
            prefs = { finished: saved.finished === true, limit: saved.limit === true };
            return prefs;
        },
        async toggle(kind) {
            prefs = read(settingsKey(), prefs);
            prefs[kind] = !prefs[kind];
            write(settingsKey(), prefs);
            if (prefs[kind]) { await unlock(); await play(kind); }
            return prefs;
        },
        async observe(states) {
            prefs = read(settingsKey(), prefs);
            const next = new Map(states.map(s => [s.id, s]));
            const before = previous;
            previous = next;
            if (!before) return;
            const candidates = [];
            for (const state of states) {
                const old = before.get(state.id);
                if (!old) continue; // Newly shared or newly visible is not a fresh notification.
                for (const kind of ['limit', 'finished']) {
                    if (prefs[kind] && state[kind] && state[kind] !== old[kind]) candidates.push({ kind, token: `${state.id}:${kind}:${state[kind]}` });
                }
            }
            // Coalesce simultaneous changes and deduplicate across tabs when Web Locks is available.
            const deliver = async () => {
                const key = `${settingsKey()}.played`;
                const recent = read(key, []);
                const fresh = candidates.filter(c => !recent.includes(c.token));
                if (!fresh.length) return;
                write(key, [...recent, ...fresh.map(c => c.token)].slice(-100));
                await play(fresh.some(c => c.kind === 'limit') ? 'limit' : 'finished');
            };
            if (navigator.locks) await navigator.locks.request(settingsKey(), deliver);
            else await deliver();
        }
    };
})();
