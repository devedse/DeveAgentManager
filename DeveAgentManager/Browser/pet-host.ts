import type { PetRenderer, PetArtModule } from "./pets/types.js";

interface PetSnapshot {
    userId: string; name: string; species: string; stage: number; stageProgress: number;
    happiness: number; size: number; reducedMotion: boolean; revision: number; reaction: string; reactionAt: string | null;
    generation: number; needsAdoption: boolean;
}
interface DotNetPet { invokeMethodAsync(method: "Pet" | "OpenMenu" | "CloseMenu" | "Dismiss"): Promise<void>; }
interface Host {
    root: HTMLElement; mount: HTMLElement; dotnet: DotNetPet; abort: AbortController;
    renderer?: PetRenderer; species?: string; pending?: string; snapshot?: PetSnapshot; visible: boolean;
    frame: number; time: number; last: number; motion: string; motionAt: number;
    stage: number; stageAt: number; targetStage?: number; revision?: number; key: string; loadVersion: number;
    gaze: { x: number; y: number }; x: number; y: number;
    threatened: boolean; nextIdleAt: number; retiringAt?: number; retiringReduced?: boolean; requestedRenderer?: string; adopting?: boolean;
}
const hosts = new WeakMap<HTMLElement, Host>();
const artModules = new Map<string, Promise<PetArtModule>>();
const durations: Record<string, number> = { happy: 3.3, wave: 3.2, hop: 3.15, peck: 2.6, walk: 4.5, surprised: 2.4, hatch: 2 };

export function initialize(root: HTMLElement, mount: HTMLElement, dotnet: DotNetPet): void {
    const h: Host = { root, mount, dotnet, abort: new AbortController(), visible: false, frame: 0, time: 0, last: 0,
        motion: "idle", motionAt: 0, stage: -1, stageAt: 0, key: "", loadVersion: 0, gaze: { x: 0, y: 0 }, x: 0, y: 0,
        threatened: false, nextIdleAt: 8 + Math.random() * 10 };
    hosts.set(root, h);
    const body = root.querySelector<HTMLElement>(".dam-pet-body")!;
    const signal = h.abort.signal;
    let drag: { id: number; startX: number; startY: number; x: number; y: number; moved: boolean } | null = null;
    let longPress = 0, suppressClick = false;
    const call = (method: "Pet" | "OpenMenu" | "CloseMenu" | "Dismiss") => { void dotnet.invokeMethodAsync(method).catch(() => {}); };
    const save = () => { try { localStorage.setItem(h.key, JSON.stringify({ x: h.x / innerWidth, y: h.y / innerHeight })); } catch { /* Storage may be unavailable. */ } };
    const cancel = () => { clearTimeout(longPress); drag = null; root.classList.remove("dragging"); };
    body.addEventListener("pointerdown", e => {
        if (e.button !== 0) return;
        drag = { id: e.pointerId, startX: e.clientX, startY: e.clientY, x: h.x, y: h.y, moved: false };
        suppressClick = false; body.setPointerCapture(e.pointerId);
        if (e.pointerType === "touch") longPress = window.setTimeout(() => { suppressClick = true; cancel(); call("OpenMenu"); }, 650);
    }, { signal });
    body.addEventListener("pointermove", e => {
        if (!drag || e.pointerId !== drag.id) return;
        const dx = e.clientX - drag.startX, dy = e.clientY - drag.startY;
        if (Math.hypot(dx, dy) > 7) { drag.moved = true; clearTimeout(longPress); root.classList.add("dragging"); }
        if (drag.moved) { h.x = drag.x + dx; h.y = drag.y + dy; clamp(h); }
    }, { signal });
    body.addEventListener("pointerup", e => {
        if (!drag || e.pointerId !== drag.id) return;
        const moved = drag.moved;
        const target = root.querySelector<HTMLElement>(".dam-pet-hide-target")!.getBoundingClientRect();
        const dismiss = moved && e.clientX >= target.left && e.clientX <= target.right && e.clientY >= target.top && e.clientY <= target.bottom;
        suppressClick = moved; cancel(); if (moved) save(); if (dismiss) call("Dismiss");
    }, { signal });
    body.addEventListener("pointercancel", cancel, { signal });
    body.addEventListener("click", e => { if (h.retiringAt !== undefined) return; if (suppressClick) { e.preventDefault(); suppressClick = false; } else call("Pet"); }, { signal });
    body.addEventListener("contextmenu", e => { e.preventDefault(); cancel(); call("OpenMenu"); }, { signal });
    body.addEventListener("keydown", e => {
        if (e.key === "ContextMenu" || (e.shiftKey && e.key === "F10")) { e.preventDefault(); call("OpenMenu"); }
        else if (e.key === "Enter" || e.key === " ") { e.preventDefault(); call("Pet"); }
        else if (e.key === "Escape") cancel();
        else if (e.key.startsWith("Arrow")) { e.preventDefault(); h.x += e.key === "ArrowRight" ? 12 : e.key === "ArrowLeft" ? -12 : 0; h.y += e.key === "ArrowDown" ? 12 : e.key === "ArrowUp" ? -12 : 0; clamp(h); save(); }
    }, { signal });
    document.addEventListener("pointerdown", e => { if (!root.contains(e.target as Node) && root.querySelector(".dam-pet-menu")) call("CloseMenu"); }, { signal });
    root.addEventListener("keydown", e => { if (e.key === "Escape") { call("CloseMenu"); body.focus(); } }, { signal });
    document.addEventListener("pointermove", e => {
        const b = root.getBoundingClientRect(); h.gaze = { x: Math.max(-1, Math.min(1, (e.clientX - b.left - b.width / 2) / 350)), y: Math.max(-1, Math.min(1, (e.clientY - b.top - b.height / 2) / 350)) };
    }, { passive: true, signal });
    window.addEventListener("resize", () => clamp(h), { signal });
    window.visualViewport?.addEventListener("resize", () => clamp(h), { signal });
    document.addEventListener("visibilitychange", () => { h.last = 0; if (document.hidden) { cancelAnimationFrame(h.frame); h.frame = 0; } else start(h); }, { signal });
}
function clamp(h: Host): void {
    const viewport = window.visualViewport;
    const width = viewport?.width ?? innerWidth, height = viewport?.height ?? innerHeight;
    const size = h.snapshot?.size ?? 140;
    h.x = Math.max(0, Math.min(width - size, h.x)); h.y = Math.max(viewport?.offsetTop ?? 0, Math.min(height + (viewport?.offsetTop ?? 0) - size - 24, h.y));
    h.root.style.left = h.x + "px"; h.root.style.top = h.y + "px";
}
export async function update(root: HTMLElement, snapshot: PetSnapshot, rendererId: string, visible: boolean, threatened = false): Promise<void> {
    const h = hosts.get(root); if (!h) return;
    const previous = h.snapshot;
    h.threatened = threatened; h.requestedRenderer = rendererId;
    if (previous && previous.generation !== snapshot.generation) {
        if (!previous.needsAdoption && snapshot.needsAdoption && h.renderer && visible) {
            h.retiringAt = h.time; h.retiringReduced = snapshot.reducedMotion || matchMedia("(prefers-reduced-motion: reduce)").matches;
            h.targetStage = undefined; root.dataset.retiring = "true";
        }
        if (previous.needsAdoption && !snapshot.needsAdoption) h.adopting = true;
    }
    h.visible = visible; h.snapshot = snapshot; root.style.width = snapshot.size + "px";
    const key = "dam-pet-position:" + snapshot.userId;
    if (h.key !== key) {
        h.key = key; h.x = innerWidth - snapshot.size - 24; h.y = innerHeight - snapshot.size - 36;
        try { const position = JSON.parse(localStorage.getItem(key) ?? "null"); if (Number.isFinite(position?.x) && Number.isFinite(position?.y)) { h.x = position.x * innerWidth; h.y = position.y * innerHeight; } } catch { }
    }
    clamp(h);
    const menu = root.querySelector<HTMLElement>(".dam-pet-menu");
    if (menu) {
        menu.style.top = ""; menu.style.bottom = "100%"; menu.style.right = "0";
        if (menu.getBoundingClientRect().top < 8) { menu.style.bottom = "auto"; menu.style.top = "100%"; }
        const bounds = menu.getBoundingClientRect();
        if (bounds.left < 8) menu.style.right = (bounds.left - 8) + "px";
        if (bounds.bottom > innerHeight - 8) menu.style.maxHeight = Math.max(90, innerHeight - bounds.top - 16) + "px";
        if (!menu.dataset.focused) { menu.dataset.focused = "true"; menu.querySelector<HTMLElement>("button")?.focus(); }
    }
    if (!visible) { ++h.loadVersion; h.pending = undefined; cancelAnimationFrame(h.frame); h.frame = 0; h.last = 0; return; }
    if (h.retiringAt !== undefined) { start(h); return; }
    if (h.species !== rendererId) {
        if (h.pending === rendererId) return;
        h.pending = rendererId; const version = ++h.loadVersion;
        try {
            if (!/^[a-z][a-z0-9-]*$/.test(rendererId)) throw new Error("Invalid renderer id.");
            let loading = artModules.get(rendererId);
            if (!loading) {
                loading = import(new URL("./pets/" + rendererId + ".js", import.meta.url).href).then(async (module: PetArtModule) => { await module.prepare?.(); return module; });
                artModules.set(rendererId, loading);
            }
            const art = await loading;
            if (h.abort.signal.aborted || h.loadVersion !== version) return;
            h.renderer?.dispose?.(); h.mount.replaceChildren(); h.renderer = art.createRenderer(h.mount, { small: true });
            const hatch = h.adopting && !snapshot.reducedMotion && !matchMedia("(prefers-reduced-motion: reduce)").matches;
            h.species = rendererId; h.targetStage = hatch ? snapshot.stage : undefined; h.stage = hatch ? 0 : snapshot.stage;
            h.renderer.setStage(h.stage); h.revision = undefined; h.adopting = false;
            if (hatch) { h.stageAt = h.time; h.motion = "hatch"; h.motionAt = h.time; }
            root.removeAttribute("data-art-error");
        } catch {
            artModules.delete(rendererId); root.dataset.artError = "Artwork could not load. Open pet settings to try again.";
            return;
        } finally { if (h.loadVersion === version) h.pending = undefined; }
    }
    if (!h.renderer) return;
    if (h.stage !== snapshot.stage && h.targetStage !== snapshot.stage) {
        const reduced = snapshot.reducedMotion || matchMedia("(prefers-reduced-motion: reduce)").matches;
        if (h.stage === 0 && snapshot.stage > 0 && !reduced) { h.targetStage = snapshot.stage; h.stageAt = h.time; h.motion = "hatch"; }
        else { h.targetStage = undefined; h.stage = snapshot.stage; h.renderer.setStage(h.stage); h.motion = "happy"; }
        h.motionAt = h.time;
    }
    if (h.revision !== snapshot.revision) {
        if (h.targetStage === undefined && snapshot.reactionAt && Date.now() - Date.parse(snapshot.reactionAt) < 8000 && !["working", "idle", "retired"].includes(snapshot.reaction)) {
            h.motion = snapshot.reaction === "level" || snapshot.reaction === "adopted" ? "happy" : snapshot.reaction; h.motionAt = h.time;
        }
        h.revision = snapshot.revision;
    }
    start(h);
}
function start(h: Host): void {
    if (h.frame || !h.visible || document.hidden || !h.renderer) return;
    const draw = (timestamp: number) => {
        h.frame = 0; if (!h.visible || document.hidden || h.abort.signal.aborted) return;
        const reduced = h.snapshot!.reducedMotion || matchMedia("(prefers-reduced-motion: reduce)").matches;
        if (h.last) h.time += Math.min(.05, (timestamp - h.last) / 1000); h.last = timestamp;
        if (h.retiringAt !== undefined) {
            const elapsed = h.time - h.retiringAt, sleep = h.retiringReduced ? .6 : 1.7, fade = h.retiringReduced ? .2 : 1;
            h.mount.style.opacity = String(Math.max(0, 1 - Math.max(0, elapsed - sleep) / fade));
            h.renderer!.pose({ time: h.time, motion: "sleep", elapsed, gaze: { x: 0, y: 0 }, animate: !h.retiringReduced, showRig: false, progress: 0, transitioning: false });
            if (elapsed >= sleep + fade) {
                h.retiringAt = undefined; h.mount.style.opacity = ""; delete h.root.dataset.retiring;
                h.renderer?.dispose?.(); h.renderer = undefined; h.species = undefined; h.mount.replaceChildren();
                h.motion = "idle"; h.motionAt = h.time; h.stage = -1;
                void update(h.root, h.snapshot!, h.requestedRenderer!, h.visible, h.threatened);
            } else h.frame = requestAnimationFrame(draw);
            return;
        }
        if (h.targetStage !== undefined && (reduced || h.time - h.stageAt > 1.1)) {
            h.stage = h.targetStage; h.targetStage = undefined; h.renderer!.setStage(h.stage); h.motion = "happy"; h.motionAt = h.time;
        }
        if (h.time - h.motionAt > (durations[h.motion] ?? 3.3) && h.motion !== "sleep") h.motion = "idle";
        if (!reduced && !h.threatened && !h.snapshot!.needsAdoption && h.targetStage === undefined && h.motion === "idle" && h.time >= h.nextIdleAt) {
            const actions = ["peck", "peck", "walk", "wave", "hop"];
            h.motion = actions[Math.floor(Math.random() * actions.length)]; h.motionAt = h.time; h.nextIdleAt = h.time + 12 + Math.random() * 14;
        }
        const motion = h.threatened && !h.snapshot!.needsAdoption ? "sad" : h.motion === "working" ? "idle" : h.motion === "idle" && h.snapshot!.happiness < 25 ? "sad" : h.motion;
        h.root.dataset.motion = motion;
        h.renderer!.pose({ time: reduced ? 0 : h.time, motion, elapsed: reduced ? .8 : h.time - h.motionAt, gaze: reduced ? { x: 0, y: 0 } : h.gaze,
            animate: !reduced, showRig: false, progress: h.snapshot!.stageProgress, transitioning: h.targetStage !== undefined });
        if (!reduced) h.frame = requestAnimationFrame(draw);
    };
    h.frame = requestAnimationFrame(draw);
}
export function dispose(root: HTMLElement): void {
    const h = hosts.get(root); if (!h) return;
    h.abort.abort(); ++h.loadVersion; cancelAnimationFrame(h.frame); h.renderer?.dispose?.(); hosts.delete(root);
}
