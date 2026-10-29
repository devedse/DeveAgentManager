interface CodexVoiceState {
    host: HTMLElement;
    dotnet: DotNetReference;
    agent: string;
    generation: number;
    phase: "idle" | "permission" | "connecting" | "connected";
    muted: boolean;
    playbackBlocked: boolean;
    disposed: boolean;
    terminalMode: boolean;
    speakerSource: MediaStreamAudioSourceNode | null;
    speakerAnalyser: AnalyserNode | null;
    speakerSamples: Float32Array<ArrayBuffer> | null;
    lastLevelSent: number;
    signalingStarted: boolean;
    stream: MediaStream | null;
    peer: RTCPeerConnection | null;
    socket: WebSocket | null;
    listener: WebSocket | null;
    listenerReady: boolean;
    listenTimer: ReturnType<typeof setTimeout> | null;
    audio: HTMLAudioElement | null;
    sender: RTCRtpSender | null;
    changingInput: boolean;
    inputContext: AudioContext | null;
    inputSource: MediaStreamAudioSourceNode | null;
    inputAnalyser: AnalyserNode | null;
    inputFrame: number;
    transcriptObserver: MutationObserver | null;
    transcriptResizeObserver: ResizeObserver | null;
    transcriptScroll: () => void;
    transcriptFrame: number;
    followTranscript: boolean;
    timer: ReturnType<typeof setTimeout> | null;
    key: (event: KeyboardEvent) => void;
    hidden: () => void;
    leaving: () => void;
    devicesChanged: () => void;
}

const voices = new WeakMap<HTMLElement, CodexVoiceState>();
let active: CodexVoiceState | null = null;

async function report(state: CodexVoiceState, error: string | null = null) {
    if (state.disposed) return;
    try { await state.dotnet.invokeMethodAsync("VoiceChanged", state.phase, state.muted, state.playbackBlocked, error); }
    catch { dispose(state.host); }
}

export function initialize(host: HTMLElement, dotnet: DotNetReference, agent: string, terminalMode = false) {
    const state: CodexVoiceState = { host, dotnet, agent, generation: 0, phase: "idle", muted: false,
        playbackBlocked: false, disposed: false, stream: null, peer: null, socket: null, listener: null, listenerReady: false, listenTimer: null, audio: null, timer: null,
        terminalMode, speakerSource: null, speakerAnalyser: null, speakerSamples: null, lastLevelSent: 0, signalingStarted: false,
        sender: null, changingInput: false, inputContext: null, inputSource: null, inputAnalyser: null, inputFrame: 0,
        transcriptObserver: null, transcriptResizeObserver: null, transcriptScroll: null, transcriptFrame: 0, followTranscript: true,
        key: null, hidden: null, leaving: null, devicesChanged: null };
    state.key = event => {
        if (event.key !== "F8" || event.repeat || event.ctrlKey || event.metaKey || event.altKey || !host.getClientRects().length) return;
        event.preventDefault();
        event.stopPropagation();
        if (state.phase === "idle") void start(host);
        else stop(host);
    };
    state.hidden = () => { if (document.hidden) stop(host); else listen(state); };
    state.leaving = () => stop(host);
    state.devicesChanged = () => { if (state.stream) void reportInputs(state); };
    voices.set(host, state);
    document.addEventListener("keydown", state.key, true);
    document.addEventListener("visibilitychange", state.hidden);
    window.addEventListener("pagehide", state.leaving);
    navigator.mediaDevices?.addEventListener("devicechange", state.devicesChanged);
    listen(state);
    const transcript = host.querySelector<HTMLElement>(".terminal-voice-transcript-lines");
    if (transcript) {
        state.transcriptScroll = () => { state.followTranscript = transcript.scrollHeight - transcript.clientHeight - transcript.scrollTop <= 24; };
        transcript.addEventListener("scroll", state.transcriptScroll);
        const follow = () => {
            if (!state.followTranscript) return;
            cancelAnimationFrame(state.transcriptFrame);
            state.transcriptFrame = requestAnimationFrame(() => { if (!state.disposed && state.followTranscript) transcript.scrollTop = transcript.scrollHeight; });
        };
        state.transcriptObserver = new MutationObserver(follow);
        state.transcriptObserver.observe(transcript, { childList: true, characterData: true, subtree: true });
        state.transcriptResizeObserver = new ResizeObserver(follow);
        state.transcriptResizeObserver.observe(transcript);
    }
}

// Signaling only: viewing a terminal never opens the microphone or starts a call.
function listen(state: CodexVoiceState) {
    clearTimeout(state.listenTimer);
    if (state.disposed || !state.terminalMode || document.hidden || state.phase !== "idle" || state.listener) return;
    const url = new URL(`ws/agents/${state.agent}/voice`, document.baseURI);
    url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
    const socket = state.listener = new WebSocket(url);
    state.listenerReady = false;
    socket.onopen = () => {
        if (state.listener === socket) socket.send(JSON.stringify({ type: "listen", mode: "terminal" }));
    };
    socket.onmessage = event => {
        if (state.listener !== socket || state.disposed || document.hidden) return;
        try {
            const message: { type?: string } = JSON.parse(event.data);
            if (message.type === "ready") state.listenerReady = true;
            else if (message.type === "requested" && state.listenerReady) void start(state.host, true);
        } catch { socket.close(); }
    };
    socket.onclose = () => {
        if (state.listener === socket) { state.listener = null; state.listenerReady = false; }
    };
    // No automatic retry after transport loss, permission failure, or another tab owning voice.
    socket.onerror = () => socket.close();
}

function releaseListener(socket: WebSocket): Promise<void> {
    return new Promise(resolve => {
        if (socket.readyState === WebSocket.CLOSED) { resolve(); return; }
        const timer = setTimeout(resolve, 3000);
        socket.addEventListener("close", () => { clearTimeout(timer); resolve(); }, { once: true });
        socket.close();
    });
}

const microphonePreference = "dam-voice-microphone";

function preferredInput(): string | null {
    try { return localStorage.getItem(microphonePreference); } catch { return null; }
}

function captureInput(deviceId: string | null) {
    return navigator.mediaDevices.getUserMedia({ audio: {
        echoCancellation: true, noiseSuppression: true, autoGainControl: true,
        ...(deviceId ? { deviceId: { exact: deviceId } } : {}),
    } });
}

async function reportInputs(state: CodexVoiceState) {
    const stream = state.stream;
    if (!stream || state.disposed) return;
    try {
        const devices = (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === "audioinput");
        if (stream !== state.stream || state.disposed) return;
        await state.dotnet.invokeMethodAsync("VoiceInputsChanged",
            devices.map((device, index) => ({ id: device.deviceId, label: device.label || `Microphone ${index + 1}` })),
            stream.getAudioTracks()[0]?.getSettings().deviceId ?? devices[0]?.deviceId ?? "");
    } catch { /* Capture can continue when device enumeration is unavailable. */ }
}

function monitorInput(state: CodexVoiceState, stream: MediaStream) {
    cancelAnimationFrame(state.inputFrame);
    state.inputSource?.disconnect();
    state.inputAnalyser?.disconnect();
    const track = stream.getAudioTracks()[0];
    track?.addEventListener("ended", () => {
        if (state.stream === stream) stop(state.host, "Microphone disconnected. Connect it and start voice again.");
    });
    let samples: Float32Array<ArrayBuffer> | null = null;
    try {
        const context = state.inputContext;
        if (context) {
            state.inputSource = context.createMediaStreamSource(stream);
            state.inputAnalyser = context.createAnalyser();
            state.inputAnalyser.fftSize = 256;
            state.inputSource.connect(state.inputAnalyser);
            samples = new Float32Array(state.inputAnalyser.fftSize);
            void context.resume().catch(() => {});
        }
    } catch { state.inputAnalyser = null; }
    const update = () => {
        if (state.stream !== stream || state.disposed) return;
        let level = 0;
        const available = state.inputContext?.state === "running" && state.inputAnalyser && samples;
        if (available && !state.muted && !track?.muted) {
            state.inputAnalyser.getFloatTimeDomainData(samples);
            const rms = Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length);
            level = Math.min(100, Math.round(rms * 600));
        }
        state.host.style.setProperty("--dam-microphone-level", `${level}%`);
        if (state.terminalMode && state.signalingStarted && state.socket?.readyState === WebSocket.OPEN && performance.now() - state.lastLevelSent >= 100) {
            let speakerPeak = 0;
            if (state.speakerAnalyser && state.speakerSamples && !state.playbackBlocked && state.audio && !state.audio.paused && !state.audio.muted) {
                state.speakerAnalyser.getFloatTimeDomainData(state.speakerSamples);
                speakerPeak = Math.min(65535, Math.round(Math.sqrt(state.speakerSamples.reduce((sum, value) => sum + value * value, 0) / state.speakerSamples.length) * 65535));
            }
            if (state.socket.bufferedAmount < 8192) state.socket.send(JSON.stringify({ type: "levels", microphonePeak: Math.round(level * 655.35), speakerPeak }));
            state.lastLevelSent = performance.now();
        }
        const meter = state.host.querySelector<HTMLElement>(".terminal-voice-input-level");
        if (meter) {
            meter.setAttribute("aria-valuenow", String(level));
            meter.setAttribute("aria-valuetext", state.muted ? "Microphone muted" : track?.muted ? "Microphone is not delivering audio"
                : !available ? "Microphone level unavailable" : `${level}% microphone input`);
        }
        state.inputFrame = requestAnimationFrame(update);
    };
    update();
}

export async function start(host: HTMLElement, terminalRequested = false) {
    const state = voices.get(host);
    if (!state || state.disposed || state.phase !== "idle") return;
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        await report(state, "Voice needs HTTPS and a browser with microphone support.");
        return;
    }
    if (active && active !== state) stop(active.host);
    active = state;
    clearTimeout(state.listenTimer);
    // Adopt the ready listener so /voice and the button share one owner and audio socket.
    const listening = state.listenerReady ? state.listener : null;
    const released = state.listener && !listening ? releaseListener(state.listener) : Promise.resolve();
    state.listener = null;
    state.listenerReady = false;
    state.socket = listening;
    const generation = ++state.generation;
    const current = () => !state.disposed && generation === state.generation;
    if (listening) {
        listening.onclose = () => { if (current()) stop(host, "Voice disconnected. Start voice to try again."); };
        listening.onerror = () => { if (current()) stop(host, "Voice could not connect. Start voice to try again."); };
    }
    state.phase = "permission";
    state.muted = false;
    state.playbackBlocked = false;
    void report(state);
    try {
        // Open during the button's user gesture; the meter must not depend on autoplay being unlocked later.
        try { state.inputContext = new (window.AudioContext || window.webkitAudioContext)(); void state.inputContext.resume().catch(() => {}); }
        catch { state.inputContext = null; }
        const preferred = preferredInput();
        let stream: MediaStream;
        try { stream = await captureInput(preferred); }
        catch (error) {
            if (!current()) return;
            if (!preferred || !(error instanceof DOMException) || !["NotFoundError", "OverconstrainedError"].includes(error.name)) throw error;
            stream = await captureInput(null);
        }
        if (!current()) { stream.getTracks().forEach(track => track.stop()); return; }
        state.stream = stream;
        monitorInput(state, stream);
        const terminalMode = await state.dotnet.invokeMethodAsync("GetVoiceMode");
        if (!current()) return;
        if (typeof terminalMode !== "boolean") { stop(host, "Voice settings could not load. Check your connection and try again."); return; }
        if (terminalRequested && !terminalMode) { stop(host, "Terminal voice is disabled. Enable it under My settings → Voice."); return; }
        state.terminalMode = terminalMode;
        if (!terminalMode && listening) {
            listening.onclose = null;
            listening.onerror = null;
            await releaseListener(listening);
            if (!current()) return;
            state.socket = null;
        }
        void reportInputs(state);
        state.phase = "connecting";
        void report(state);
        const peer = state.peer = new RTCPeerConnection();
        const audio = state.audio = document.createElement("audio");
        audio.autoplay = true;
        host.appendChild(audio);
        peer.ontrack = event => {
            if (!current()) return;
            audio.srcObject = new MediaStream([event.track]);
            if (state.terminalMode && state.inputContext) {
                try {
                    state.speakerSource?.disconnect();
                    state.speakerAnalyser?.disconnect();
                    state.speakerSource = state.inputContext.createMediaStreamSource(audio.srcObject);
                    state.speakerAnalyser = state.inputContext.createAnalyser();
                    state.speakerAnalyser.fftSize = 256;
                    state.speakerSource.connect(state.speakerAnalyser);
                    state.speakerSamples = new Float32Array(256);
                } catch { /* Speaker levels are optional; playback remains on the media element. */ }
            }
            void play(host);
        };
        peer.onconnectionstatechange = () => {
            if (current() && (peer.connectionState === "failed" || peer.connectionState === "disconnected"))
                stop(host, "Voice disconnected. You can start it again.");
        };
        for (const track of stream.getAudioTracks()) state.sender = peer.addTrack(track, stream);
        const channel = peer.createDataChannel("oai-events");
        channel.onopen = () => {
            if (!current()) return;
            if (state.terminalMode) {
                if (state.socket?.readyState === WebSocket.OPEN) state.socket.send(JSON.stringify({ type: "connected" }));
                return;
            }
            clearTimeout(state.timer);
            state.phase = "connected";
            void report(state);
        };
        channel.onmessage = event => {
            if (!current()) return;
            try {
                const message: { type?: string; turn_id?: string; delta?: string; turn?: { id?: string; role?: string; transcript?: string } } = JSON.parse(event.data);
                if (message.type === "session.closed") stop(host);
                else if (message.type === "error" || message.type === "session.error") stop(host, "Codex voice failed. You can start it again.");
                else if (!state.terminalMode && (message.type === "turn.created" || message.type === "turn.done")
                    && typeof message.turn?.id === "string" && message.turn.id.length <= 128
                    && (message.turn.role === "user" || message.turn.role === "assistant") && typeof message.turn.transcript === "string"
                    && message.turn.transcript.length <= 65536) {
                    void state.dotnet.invokeMethodAsync("VoiceTranscriptChanged", { id: `${generation}:${message.turn.id}`, role: message.turn.role,
                        text: message.turn.transcript, append: false, complete: message.type === "turn.done" }).catch(() => {});
                } else if (!state.terminalMode && message.type === "turn.delta" && typeof message.turn_id === "string" && message.turn_id.length <= 128
                    && typeof message.delta === "string" && message.delta.length <= 65536) {
                    void state.dotnet.invokeMethodAsync("VoiceTranscriptChanged", { id: `${generation}:${message.turn_id}`, role: null,
                        text: message.delta, append: true, complete: false }).catch(() => {});
                }
            } catch { /* Media and delegation stay owned by Codex; no provider payloads enter the UI. */ }
        };
        await peer.setLocalDescription(await peer.createOffer());
        if (!current()) return;
        const url = new URL(`ws/agents/${state.agent}/voice`, document.baseURI);
        url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        await released;
        if (!current()) return;
        const socket = state.socket = (state.terminalMode ? listening : null) ?? new WebSocket(url);
        let answered = false;
        const sendOffer = () => {
            if (current()) {
                socket.send(JSON.stringify({ type: "start", sdp: peer.localDescription.sdp, ...(state.terminalMode ? { mode: "terminal" } : {}) }));
                state.signalingStarted = true;
            }
        };
        socket.onmessage = async event => {
            if (!current()) return;
            try {
                const message: { type: string; sdp?: string; message?: string; microphoneMuted?: boolean; speakerSuppressed?: boolean } = JSON.parse(event.data);
                if (message.type === "error") { stop(host, message.message || "Voice could not connect."); return; }
                if (message.type === "answer" && !answered && typeof message.sdp === "string") {
                    answered = true;
                    await peer.setRemoteDescription({ type: "answer", sdp: message.sdp });
                }
                else if (state.terminalMode && message.type === "devices") {
                    socket.send(JSON.stringify({ type: "devicesOpened", available: channel.readyState === "open"
                        && state.stream.getAudioTracks().some(track => track.readyState === "live") }));
                }
                else if (state.terminalMode && message.type === "controls" && typeof message.microphoneMuted === "boolean" && typeof message.speakerSuppressed === "boolean") {
                    state.muted = message.microphoneMuted;
                    state.stream.getAudioTracks().forEach(track => { track.enabled = !state.muted; });
                    audio.muted = message.speakerSuppressed;
                    socket.send(JSON.stringify({ type: "controlsApplied" }));
                    void report(state);
                }
                else if (state.terminalMode && message.type === "active") {
                    clearTimeout(state.timer);
                    state.phase = "connected";
                    void report(state);
                }
            } catch { if (current()) stop(host, "Voice could not connect. You can start it again."); }
        };
        socket.onopen = sendOffer;
        if (socket.readyState === WebSocket.OPEN) sendOffer();
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

export async function setMicrophone(host: HTMLElement, deviceId: string): Promise<string | null> {
    const state = voices.get(host);
    if (!state?.stream || !state.sender || state.changingInput) return null;
    state.changingInput = true;
    const generation = state.generation;
    let stream: MediaStream | null = null;
    try {
        stream = await captureInput(deviceId);
        if (state.disposed || state.generation !== generation) { stream.getTracks().forEach(track => track.stop()); return null; }
        const track = stream.getAudioTracks()[0];
        if (!track) throw new Error("No audio track");
        track.enabled = !state.muted;
        await state.sender.replaceTrack(track);
        if (state.disposed || state.generation !== generation) { stream.getTracks().forEach(track => track.stop()); return null; }
        const previous = state.stream;
        state.stream = stream;
        monitorInput(state, stream);
        previous.getTracks().forEach(track => track.stop());
        try { localStorage.setItem(microphonePreference, deviceId); } catch { }
        await reportInputs(state);
        return null;
    } catch {
        stream?.getTracks().forEach(track => track.stop());
        return state.generation === generation ? "That microphone could not open. Your previous microphone is still selected." : null;
    } finally { if (state.generation === generation) state.changingInput = false; }
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
    const generation = ++state.generation;
    const previousSocket = state.socket ?? state.listener;
    clearTimeout(state.listenTimer);
    state.listener?.close();
    state.listener = null;
    state.listenerReady = false;
    state.signalingStarted = false;
    clearTimeout(state.timer);
    cancelAnimationFrame(state.inputFrame);
    state.speakerSource?.disconnect();
    state.speakerSource = null;
    state.speakerAnalyser?.disconnect();
    state.speakerAnalyser = null;
    state.speakerSamples = null;
    state.inputSource?.disconnect();
    state.inputSource = null;
    state.inputAnalyser?.disconnect();
    state.inputAnalyser = null;
    void state.inputContext?.close().catch(() => {});
    state.inputContext = null;
    state.changingInput = false;
    state.sender = null;
    state.host.style.removeProperty("--dam-microphone-level");
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
    if (!error && !state.disposed && !document.hidden) {
        // Wait for server cleanup and owner release before accepting the next terminal request.
        const released = previousSocket ? releaseListener(previousSocket) : Promise.resolve();
        void released.then(() => {
            if (state.generation === generation && !state.disposed && !document.hidden)
                state.listenTimer = setTimeout(() => listen(state), 0);
        });
    }
}

export function dispose(host: HTMLElement) {
    const state = voices.get(host);
    if (!state) return;
    state.disposed = true;
    stop(host);
    document.removeEventListener("keydown", state.key, true);
    document.removeEventListener("visibilitychange", state.hidden);
    window.removeEventListener("pagehide", state.leaving);
    navigator.mediaDevices?.removeEventListener("devicechange", state.devicesChanged);
    state.transcriptObserver?.disconnect();
    state.transcriptResizeObserver?.disconnect();
    cancelAnimationFrame(state.transcriptFrame);
    if (state.transcriptScroll) host.querySelector(".terminal-voice-transcript-lines")?.removeEventListener("scroll", state.transcriptScroll);
    voices.delete(host);
}
