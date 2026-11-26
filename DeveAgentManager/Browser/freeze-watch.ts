// Notices when the Portal stops responding: an endless loop or long work blocks the page, so nothing on it moves (not
// even the Damagochi) and even Reload waits. The page tells a worker every second that it still answers, and what it
// does meanwhile (clicks, navigations, slow frames, sections loading). The worker keeps running while the page doesn't:
// when the page hasn't answered for 10 seconds while shown, it writes what happened before to the Manager's log, and
// again if the page answers again. A freeze that can't be reproduced on demand still leaves a trace. See
// docs/portal-freezes.md.
(() => {
    let worker: Worker;
    try {
        worker = new Worker(new URL("freeze-watch-worker.js", (document.currentScript as HTMLScriptElement).src));
    } catch {
        return;
    }

    const post = (message: object) => {
        try { worker.postMessage(message); } catch { /* The worker is gone: nothing more to watch. */ }
    };
    const note = (kind: string, detail?: string) => post({ type: "note", kind, detail: detail?.slice(0, 200) });
    window.damFreezeWatch = { note };

    let beats = 0, elements: number | null = null;
    const beat = () => {
        // Counting the elements walks the page: every few seconds is enough.
        if (beats++ % 5 === 0) elements = document.getElementsByTagName("*").length;
        post({
            type: "beat",
            visible: document.visibilityState === "visible",
            page: location.pathname,
            elements,
            jsHeap: (performance as any).memory?.usedJSHeapSize ?? null,
            wasm: (window as any).Blazor?.runtime?.Module?.HEAPU8?.length ?? null,
            openFor: Math.round(performance.now() / 1000),
        });
    };
    setInterval(beat, 1000);
    beat();
    document.addEventListener("visibilitychange", () => {
        note(document.visibilityState === "visible" ? "shown" : "hidden");
        beat();
    });

    // What was clicked: a button's label or a link's path, not the page's text (a conversation stays out of the log).
    document.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const control = target?.closest("a[href], button, [role=button], [role=menuitem], [role=tab], [role=option], input, select, textarea");
        if (control instanceof HTMLAnchorElement) {
            note("click", `link → ${new URL(control.href, location.href).pathname}`);
        } else if (control) {
            const label = control.getAttribute("aria-label") ?? (control.matches("input, select, textarea") ? control.getAttribute("name") ?? control.tagName : control.textContent);
            note("click", `${control.tagName.toLowerCase()} "${(label ?? "").replace(/\s+/g, " ").trim().slice(0, 40)}"`);
        } else {
            note("click", target ? target.tagName.toLowerCase() : "page");
        }
    }, true);

    const navigation = (window as any).navigation;
    if (navigation?.addEventListener) {
        navigation.addEventListener("navigate", (event: any) =>
            note(event.navigationType === "traverse" ? "back or forward" : event.navigationType, new URL(event.destination.url).pathname));
    } else {
        window.addEventListener("popstate", () => note("back or forward", location.pathname));
    }
    window.addEventListener("error", event => note("error", event.message));
    window.addEventListener("unhandledrejection", event => note("error", String(event.reason?.message ?? event.reason)));

    // Frames the page took long to draw, with the scripts that took the time (Chromium). A freeze that ends is one too.
    try {
        new PerformanceObserver(list => {
            for (const frame of list.getEntries() as any[]) {
                if (frame.duration < 200) continue;
                const scripts = [...(frame.scripts ?? [])].sort((a, b) => b.duration - a.duration).slice(0, 3).map(script =>
                    `${script.invoker || script.invokerType} ${script.sourceFunctionName || ""} ${(script.sourceURL || "").split("/").pop()}:${script.sourceCharPosition} ${Math.round(script.duration)} ms`.replace(/\s+/g, " "));
                note("slow frame", `${Math.round(frame.duration)} ms${scripts.length ? ": " + scripts.join("; ") : ""}`);
            }
        }).observe({ type: "long-animation-frame" });
    } catch {
        try {
            new PerformanceObserver(list => {
                for (const task of list.getEntries()) if (task.duration >= 200) note("slow task", `${Math.round(task.duration)} ms`);
            }).observe({ type: "longtask" });
        } catch { /* Not measured in this browser. */ }
    }
})();
