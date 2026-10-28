interface CodexVoiceState {
    host: HTMLElement;
    dotnet: DotNetReference;
    agent: string;
    generation: number;
    phase: "idle" | "permission" | "connecting" | "connected";
    muted: boolean;
    playbackBlocked: boolean;
    disposed: boolean;
    stream: MediaStream | null;
    peer: RTCPeerConnection | null;
    socket: WebSocket | null;
    audio: HTMLAudioElement | null;
    timer: ReturnType<typeof setTimeout> | null;
    key: (event: KeyboardEvent) => void;
    hidden: () => void;
    leaving: () => void;
}

const voices = new WeakMap<HTMLElement, CodexVoiceState>();
let active: CodexVoiceState | null = null;

async function report(state: CodexVoiceState, error: string | null = null) {
    if (state.disposed) return;
    try { await state.dotnet.invokeMethodAsync("VoiceChanged", state.phase, state.muted, state.playbackBlocked, error); }
    catch { dispose(state.host); }
}

export function initialize(host: HTMLElement, dotnet: DotNetReference, agent: string) {
    const state: CodexVoiceState = { host, dotnet, agent, generation: 0, phase: "idle", muted: false,
        playbackBlocked: false, disposed: false, stream: null, peer: null, socket: null, audio: null, timer: null,
        key: null, hidden: null, leaving: null };
    state.key = event => {
        if (event.key !== "F8" || event.repeat || event.ctrlKey || event.metaKey || event.altKey || !host.getClientRects().length) return;
        event.preventDefault();
        event.stopPropagation();
        if (state.phase === "idle") void start(host);
        else stop(host);
    };
    state.hidden = () => { if (document.hidden) stop(host); };
    state.leaving = () => stop(host);
    voices.set(host, state);
    document.addEventListener("keydown", state.key, true);
    document.addEventListener("visibilitychange", state.hidden);
    window.addEventListener("pagehide", state.leaving);
}

export async function start(host: HTMLElement) {
    const state = voices.get(host);
    if (!state || state.disposed || state.phase !== "idle") return;
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        await report(state, "Voice needs HTTPS and a browser with microphone support.");
        return;
    }
    if (active && active !== state) stop(active.host);
    active = state;
    const generation = ++state.generation;
    const current = () => !state.disposed && generation === state.generation;
    state.phase = "permission";
    state.muted = false;
    state.playbackBlocked = false;
    void report(state);
    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
        if (!current()) { stream.getTracks().forEach(track => track.stop()); return; }
        state.stream = stream;
        stream.getAudioTracks().forEach(track => track.addEventListener("ended", () => {
            if (current()) stop(host, "Microphone disconnected. Connect it and start voice again.");
        }));
        state.phase = "connecting";
        void report(state);
        const peer = state.peer = new RTCPeerConnection();
        const audio = state.audio = document.createElement("audio");
        audio.autoplay = true;
        host.appendChild(audio);
        peer.ontrack = event => {
            if (!current()) return;
            audio.srcObject = new MediaStream([event.track]);
            void play(host);
        };
        peer.onconnectionstatechange = () => {
            if (current() && (peer.connectionState === "failed" || peer.connectionState === "disconnected"))
                stop(host, "Voice disconnected. You can start it again.");
        };
        for (const track of stream.getTracks()) peer.addTrack(track, stream);
        const channel = peer.createDataChannel("oai-events");
        channel.onopen = () => {
            if (!current()) return;
            clearTimeout(state.timer);
            state.phase = "connected";
            void report(state);
        };
        channel.onmessage = event => {
            if (!current()) return;
            try {
                const message: { type?: string } = JSON.parse(event.data);
                if (message.type === "session.closed") stop(host);
                else if (message.type === "error" || message.type === "session.error") stop(host, "Codex voice failed. You can start it again.");
            } catch { /* Media and delegation stay owned by Codex; no provider payloads enter the UI. */ }
        };
        await peer.setLocalDescription(await peer.createOffer());
        if (!current()) return;
        const url = new URL(`ws/agents/${state.agent}/voice`, document.baseURI);
        url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        const socket = state.socket = new WebSocket(url);
        let answered = false;
        socket.onopen = () => {
            if (current()) socket.send(JSON.stringify({ type: "start", sdp: peer.localDescription.sdp }));
        };
        socket.onmessage = async event => {
            if (!current()) return;
            try {
                const message: { type: string; sdp?: string; message?: string } = JSON.parse(event.data);
                if (message.type === "error") { stop(host, message.message || "Voice could not connect."); return; }
                if (message.type === "answer" && !answered && typeof message.sdp === "string") {
                    answered = true;
                    await peer.setRemoteDescription({ type: "answer", sdp: message.sdp });
                }
            } catch { if (current()) stop(host, "Voice could not connect. You can start it again."); }
        };
        socket.onerror = () => { if (current()) stop(host, "Voice could not connect. Check your connection and agent access."); };
        socket.onclose = event => {
            if (current()) stop(host, event.code === 1000 ? null : "Voice disconnected. You can start it again.");
        };
        state.timer = setTimeout(() => { if (current()) stop(host, "Voice took too long to connect. You can try again."); }, 45000);
    } catch (error) {
        if (!current()) return;
        const name = error instanceof DOMException ? error.name : "";
        stop(host, name === "NotAllowedError" ? "Microphone access was denied. Allow it in your browser and retry."
            : name === "NotFoundError" ? "No microphone found. Connect one and retry."
            : "The microphone or voice connection could not open. Check your devices and retry.");
    }
}

export function setMuted(host: HTMLElement, muted: boolean) {
    const state = voices.get(host);
    if (!state?.stream) return;
    state.muted = muted;
    state.stream.getAudioTracks().forEach(track => { track.enabled = !muted; });
    void report(state);
}

export async function play(host: HTMLElement) {
    const state = voices.get(host);
    if (!state?.audio) return;
    const generation = state.generation;
    try { await state.audio.play(); if (generation === state.generation) state.playbackBlocked = false; }
    catch { if (generation === state.generation) state.playbackBlocked = true; }
    if (generation !== state.generation) return;
    void report(state);
}

export function stop(host: HTMLElement, error: string | null = null) {
    const state = voices.get(host);
    if (!state) return;
    ++state.generation;
    clearTimeout(state.timer);
    state.stream?.getTracks().forEach(track => track.stop());
    state.stream = null;
    state.peer?.close();
    state.peer = null;
    if (state.socket?.readyState === WebSocket.OPEN) state.socket.send(JSON.stringify({ type: "stop" }));
    state.socket?.close();
    state.socket = null;
    state.audio?.pause();
    if (state.audio) state.audio.srcObject = null;
    state.audio?.remove();
    state.audio = null;
    state.phase = "idle";
    state.muted = false;
    state.playbackBlocked = false;
    if (active === state) active = null;
    void report(state, error);
}

export function dispose(host: HTMLElement) {
    const state = voices.get(host);
    if (!state) return;
    state.disposed = true;
    stop(host);
    document.removeEventListener("keydown", state.key, true);
    document.removeEventListener("visibilitychange", state.hidden);
    window.removeEventListener("pagehide", state.leaving);
    voices.delete(host);
}
