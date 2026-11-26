// The worker half of freeze-watch.ts. It keeps running while the page doesn't: once the page hasn't answered for
// FROZEN_AFTER while shown, it sends the Manager what the page did before (the Manager writes it to its log), and once
// the page answers again, how long it was frozen and what came after (a slow frame says which script took the time).
(() => {
    interface Note { at: number; kind: string; detail?: string }
    interface Beat { visible: boolean; page: string; elements: number | null; jsHeap: number | null; wasm: number | null; openFor: number }
    const FROZEN_AFTER = 10_000;
    // Sections load often on a busy page: they keep their own shorter list, so clicks and navigations stay in the report.
    const events: Note[] = [], loads: Note[] = [];
    let beat: Beat | null = null, answered = 0, ticked = Date.now(), frozen = false;

    const keep = (list: Note[], note: Note, most: number) => {
        list.push(note);
        if (list.length > most) list.shift();
    };

    const send = (recovered: boolean, seconds: number, since: number) => {
        const notes = [...events, ...loads].sort((a, b) => a.at - b.at)
            .map(note => ({ seconds: Math.round((note.at - since) / 100) / 10, kind: note.kind, detail: note.detail ?? null }));
        fetch(new URL("../api/diagnostics/portal-freeze", self.location.href), {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-DAM-Request": "1" },
            body: JSON.stringify({
                recovered, seconds: Math.round(seconds), page: beat?.page ?? null, openForSeconds: beat?.openFor ?? null,
                jsHeapBytes: beat?.jsHeap ?? null, wasmBytes: beat?.wasm ?? null, elements: beat?.elements ?? null,
                browser: navigator.userAgent, notes,
            }),
        }).catch(() => { /* The Manager isn't reachable: nothing to do. */ });
    };

    self.onmessage = (event: MessageEvent) => {
        const message = event.data;
        const now = Date.now();
        if (message?.type === "note") {
            const note = { at: now, kind: String(message.kind), detail: message.detail ?? undefined };
            if (note.kind === "load" || note.kind === "loaded" || note.kind === "left") keep(loads, note, 20);
            else keep(events, note, 28);
            return;
        }
        if (message?.type !== "beat") return;
        if (frozen) {
            frozen = false;
            const seconds = (now - answered) / 1000, since = answered;
            // The slow frame that was the freeze is described once it ended: give it a moment to arrive.
            setTimeout(() => send(true, seconds, since), 3000);
        }
        answered = now;
        beat = message;
    };

    setInterval(() => {
        const now = Date.now();
        // This worker's own timers were late too: the browser or the computer was suspended, not only the page.
        if (now - ticked > 5000 && answered) answered += now - ticked - 1000;
        ticked = now;
        if (!beat?.visible || frozen || !answered || now - answered < FROZEN_AFTER) return;
        frozen = true;
        send(false, (now - answered) / 1000, answered);
    }, 1000);
})();
