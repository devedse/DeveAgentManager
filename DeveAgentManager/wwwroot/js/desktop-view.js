// Frames are temporary blob URLs. No polling runs while the page is hidden or after navigation.
const viewers = new WeakMap();

export function start(host, dotnet, url) {
    stop(host);
    const state = { host, dotnet, url: new URL(url, document.baseURI), live: false, interval: 2000, stopped: false,
        controller: null, timer: null, blob: null, timestamp: null };
    viewers.set(host, state);
    state.visibility = () => {
        clearTimeout(state.timer);
        if (document.hidden) {
            state.controller?.abort();
            report(state, null);
        } else if (state.live) refresh(host);
    };
    document.addEventListener("visibilitychange", state.visibility);
    refresh(host);
}

async function report(state, error) {
    if (state.stopped) return;
    try { await state.dotnet.invokeMethodAsync("DesktopFrameUpdated", state.timestamp, error, document.hidden); }
    catch { stop(state.host); }
}

export async function refresh(host) {
    const state = viewers.get(host);
    if (!state || state.stopped || state.controller || document.hidden) return;
    clearTimeout(state.timer);
    const controller = new AbortController();
    state.controller = controller;
    let nextBlob = null;
    try {
        const response = await fetch(state.url, { credentials: "same-origin", cache: "no-store", signal: controller.signal });
        if (!response.ok || !(response.headers.get("Content-Type") || "").startsWith("image/")) {
            let message = "Could not capture the desktop. Check its status and runner connection.";
            try { const body = await response.json(); if (typeof body.detail === "string") message = body.detail; } catch {}
            throw new Error(message);
        }
        const bytes = await response.blob();
        if (state.stopped) return;
        nextBlob = URL.createObjectURL(bytes);
        const img = host.querySelector("img");
        img.src = nextBlob;
        try { await img.decode(); } catch { throw new Error("The desktop returned an unreadable image."); }
        if (state.stopped) return;
        if (state.blob) URL.revokeObjectURL(state.blob);
        state.blob = nextBlob;
        nextBlob = null;
        const captured = response.headers.get("X-DAM-Captured-At");
        const at = captured ? new Date(captured) : new Date();
        state.timestamp = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(at);
        await report(state, null);
    } catch (error) {
        const img = host.querySelector("img");
        if (state.blob) img.src = state.blob;
        else img.removeAttribute("src");
        if (error.name !== "AbortError") await report(state, error.message);
    } finally {
        if (nextBlob) URL.revokeObjectURL(nextBlob);
        state.controller = null;
        if (state.live && !state.stopped && !document.hidden)
            state.timer = setTimeout(() => refresh(host), state.interval);
    }
}

export function setLive(host, live, seconds) {
    const state = viewers.get(host);
    if (!state) return;
    state.live = live;
    state.interval = seconds === 5 ? 5000 : 2000;
    clearTimeout(state.timer);
    if (live) refresh(host);
}

export function stop(host) {
    const state = viewers.get(host);
    if (!state) return;
    state.stopped = true;
    clearTimeout(state.timer);
    state.controller?.abort();
    document.removeEventListener("visibilitychange", state.visibility);
    if (state.blob) URL.revokeObjectURL(state.blob);
    viewers.delete(host);
}
