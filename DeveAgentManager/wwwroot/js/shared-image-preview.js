const previews = new WeakMap();
const motion = () => !matchMedia('(prefers-reduced-motion: reduce)').matches;

function topmost(state) {
    const dialogs = [...document.querySelectorAll('.mud-dialog')].filter(el => el.getClientRects().length);
    return !state.stopped && state.host.isConnected && dialogs.at(-1) === state.dialog;
}

function preload(state, index) {
    if (index < 0 || index >= state.urls.length) return Promise.resolve();
    if (!state.loaded.has(index)) {
        const image = new Image();
        image.src = state.urls[index];
        state.loaded.set(index, { image, ready: image.decode().catch(() => {}) });
    }
    return state.loaded.get(index).ready;
}

function warm(state) {
    for (const index of state.loaded.keys()) if (Math.abs(index - state.index) > 1) state.loaded.delete(index);
    void preload(state, state.index - 1);
    void preload(state, state.index + 1);
}

function neighbor(state, direction) {
    const index = state.index + direction;
    if (index < 0 || index >= state.urls.length) {
        state.neighbor?.remove();
        state.neighbor = null;
        return null;
    }
    if (state.neighbor?.getAttribute('src') !== state.urls[index]) {
        state.neighbor?.remove();
        state.neighbor = document.createElement('img');
        state.neighbor.className = 'shared-image-neighbor';
        state.neighbor.src = state.urls[index];
        state.neighbor.alt = '';
        state.neighbor.draggable = false;
        state.neighbor.setAttribute('aria-hidden', 'true');
        state.host.append(state.neighbor);
    }
    return state.neighbor;
}

function reset(state) {
    for (const animation of state.animations) animation.cancel();
    state.animations = [];
    // Keep the image on its compositor layer; dropping it can leave a blank frame on mobile after cancellation.
    state.image.style.transform = 'translate3d(0,0,0)';
    state.neighbor?.remove();
    state.neighbor = null;
    delete state.host.dataset.imageBusy;
    state.host.style.height = '';
    state.busy = false;
}

async function slide(state, offset, direction, commit) {
    const width = state.host.clientWidth;
    const other = neighbor(state, direction);
    const target = commit ? -direction * width : 0;
    if (!motion()) return;
    const options = { duration: commit ? 260 : 180, easing: 'cubic-bezier(.22,.61,.36,1)', fill: 'forwards' };
    state.animations = [state.image.animate([
        { transform: `translate3d(${offset}px,0,0)` },
        { transform: `translate3d(${target}px,0,0)` }
    ], options)];
    if (other) state.animations.push(other.animate([
        { transform: `translate3d(${direction * width + offset}px,0,0)` },
        { transform: `translate3d(${direction * width + target}px,0,0)` }
    ], options));
    if (commit) {
        const image = state.loaded.get(state.index + direction)?.image;
        if (image?.naturalWidth) {
            const limit = Math.max(0, Math.min(innerHeight * .75, innerHeight - 200));
            const height = Math.max(100, image.naturalHeight * Math.min(1, width / image.naturalWidth, limit / image.naturalHeight));
            state.animations.push(state.host.animate([
                { height: `${state.host.clientHeight}px` }, { height: `${height}px` }
            ], options));
        }
    }
    await Promise.all(state.animations.map(animation => animation.finished)).catch(() => {});
}

async function change(state, direction, offset = 0, commit = true) {
    if (!direction || !topmost(state)) return;
    if (state.busy) { state.queued = direction; return; }
    const index = state.index + direction;
    commit &&= index >= 0 && index < state.urls.length;
    if (!commit && offset === 0) return;
    state.busy = true;
    state.host.style.height = `${state.host.clientHeight}px`;
    state.host.dataset.imageBusy = 'true';
    try {
        if (commit) await preload(state, index);
        if (!topmost(state)) return;
        await slide(state, offset, direction, commit);
        if (!topmost(state)) return;
        if (commit) {
            state.image.src = state.urls[index];
            await state.self.invokeMethodAsync('SelectImage', index);
            state.index = index;
        }
    } finally {
        reset(state);
        if (!state.stopped) warm(state);
        const queued = state.queued;
        state.queued = 0;
        if (queued && topmost(state)) await change(state, queued);
    }
}

export function navigate(host, direction) {
    const state = previews.get(host);
    if (state) return change(state, Math.sign(direction));
}

export function start(host, self, urls, index) {
    stop(host);
    const state = { host, self, urls, index, dialog: host.closest('.mud-dialog'), image: host.querySelector('.shared-image-preview'),
        loaded: new Map(), animations: [], pointers: new Set(), busy: false, stopped: false, queued: 0, neighbor: null, gesture: null };
    previews.set(host, state);
    host.dataset.imageReady = 'true';
    const clearGesture = () => {
        const gesture = state.gesture;
        state.gesture = null;
        if (gesture && host.hasPointerCapture(gesture.id)) host.releasePointerCapture(gesture.id);
        return gesture;
    };
    state.onKey = event => {
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.isComposing) return;
        if (event.target instanceof Element && event.target.closest('input, textarea, select, [contenteditable="true"], [role="textbox"]')) return;
        if (!topmost(state)) return;
        event.preventDefault();
        event.stopPropagation();
        void navigate(host, event.key === 'ArrowLeft' ? -1 : 1).catch(() => {});
    };
    state.onDown = event => {
        if (event.pointerType !== 'touch' && event.pointerType !== 'pen') return;
        state.pointers.add(event.pointerId);
        if (state.pointers.size > 1) { clearGesture(); if (!state.busy) reset(state); return; }
        if (state.busy || !topmost(state)) return;
        state.gesture = { id: event.pointerId, x: event.clientX, y: event.clientY, dx: 0, offset: 0, locked: false, time: performance.now() };
        host.setPointerCapture(event.pointerId);
    };
    state.onMove = event => {
        const gesture = state.gesture;
        if (!gesture || gesture.id !== event.pointerId) return;
        const dx = event.clientX - gesture.x, dy = event.clientY - gesture.y;
        if (!gesture.locked) {
            if (Math.abs(dy) > 10 && Math.abs(dy) > Math.abs(dx)) { clearGesture(); return; }
            if (Math.abs(dx) < 8 || Math.abs(dx) < Math.abs(dy) * 1.25) return;
            gesture.locked = true;
        }
        const direction = Math.sign(-dx) || 1;
        const width = host.clientWidth;
        const other = neighbor(state, direction);
        gesture.dx = dx;
        gesture.offset = other ? Math.max(-width, Math.min(width, dx)) : dx * .18;
        state.image.style.transform = `translate3d(${gesture.offset}px,0,0)`;
        if (other) other.style.transform = `translate3d(${direction * width + gesture.offset}px,0,0)`;
        if (event.cancelable) event.preventDefault();
    };
    state.onUp = event => {
        state.pointers.delete(event.pointerId);
        if (state.gesture?.id !== event.pointerId) return;
        const gesture = clearGesture();
        if (!gesture.locked) return;
        const direction = Math.sign(-gesture.dx) || 1;
        const distance = Math.abs(gesture.dx);
        const velocity = distance / Math.max(1, performance.now() - gesture.time);
        const commit = distance >= Math.max(36, host.clientWidth * .18) || (distance > 18 && velocity > .5);
        void change(state, direction, gesture.offset, commit).catch(() => {});
    };
    state.onCancel = event => {
        state.pointers.delete(event.pointerId);
        if (state.gesture?.id !== event.pointerId) return;
        const gesture = clearGesture();
        if (gesture.locked) void change(state, Math.sign(-gesture.dx) || 1, gesture.offset, false).catch(() => {});
    };
    document.addEventListener('keydown', state.onKey, true);
    host.addEventListener('pointerdown', state.onDown);
    host.addEventListener('pointermove', state.onMove, { passive: false });
    host.addEventListener('pointerup', state.onUp);
    host.addEventListener('pointercancel', state.onCancel);
    host.addEventListener('lostpointercapture', state.onCancel);
    warm(state);
}

export function stop(host) {
    const state = previews.get(host);
    if (!state) return;
    state.stopped = true;
    state.queued = 0;
    document.removeEventListener('keydown', state.onKey, true);
    host.removeEventListener('pointerdown', state.onDown);
    host.removeEventListener('pointermove', state.onMove);
    host.removeEventListener('pointerup', state.onUp);
    host.removeEventListener('pointercancel', state.onCancel);
    host.removeEventListener('lostpointercapture', state.onCancel);
    if (state.gesture && host.hasPointerCapture(state.gesture.id)) host.releasePointerCapture(state.gesture.id);
    reset(state);
    delete host.dataset.imageReady;
    previews.delete(host);
}
