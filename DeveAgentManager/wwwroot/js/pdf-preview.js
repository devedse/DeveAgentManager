// PDF.js is loaded only when a PDF is opened. The worker and its fonts/codecs are served locally at the same version.
const viewers = new WeakMap();
const assets = new URL('../lib/pdfjs/6.4.299/', import.meta.url);

export async function open(host, url) {
    await dispose(host);
    const state = { host, sequence: 0, disposed: false, pageNumber: 1, zoom: null };
    viewers.set(host, state);
    try {
        const pdfjs = await import(new URL('build/pdf.min.mjs', assets));
        if (state.disposed) return 0;
        state.pdfjs = pdfjs;
        pdfjs.GlobalWorkerOptions.workerSrc = new URL('build/pdf.worker.min.mjs', assets).href;
        state.loading = pdfjs.getDocument({ url, disableStream: true, disableAutoFetch: true,
            cMapUrl: new URL('cmaps/', assets).href, standardFontDataUrl: new URL('standard_fonts/', assets).href,
            wasmUrl: new URL('wasm/', assets).href, iccUrl: new URL('iccs/', assets).href,
            enableXfa: false });
        state.document = await state.loading.promise;
        if (state.disposed) return 0;
        state.scroll = host.parentElement;
        state.width = state.scroll.clientWidth;
        state.observer = new ResizeObserver(() => {
            const width = state.scroll.clientWidth;
            if (Math.abs(width - state.width) < 2) return;
            state.width = width;
            if (state.zoom === null) void render(host, state.pageNumber, null).catch(() => {});
        });
        state.observer.observe(state.scroll);
        return state.document.numPages;
    } catch (error) {
        if (viewers.get(host) === state) await dispose(host);
        if (error.name === 'PasswordException') throw new Error('PDF_PASSWORD_REQUIRED');
        throw error;
    }
}

export async function render(host, pageNumber, zoom) {
    const state = viewers.get(host);
    if (!state?.document || state.disposed) return;
    const sequence = ++state.sequence;
    state.renderTask?.cancel();
    state.textLayer?.cancel();
    await state.pending?.catch(() => {});
    if (state.disposed || sequence !== state.sequence) return;
    state.pageNumber = pageNumber; state.zoom = zoom;
    state.pending = draw(state, sequence);
    try { await state.pending; }
    catch (error) { if (!state.disposed && sequence === state.sequence) throw error; }
}

async function draw(state, sequence) {
    const page = await state.document.getPage(state.pageNumber);
    if (state.disposed || sequence !== state.sequence) return;
    const base = page.getViewport({ scale: 1 });
    const scale = state.zoom === null ? Math.max(1, state.scroll.clientWidth - 24) / base.width : state.zoom / 100 * 96 / 72;
    const viewport = page.getViewport({ scale });
    // Keep canvas memory bounded on phones even when a large page is zoomed in.
    const ratio = Math.min(devicePixelRatio || 1, 2, Math.sqrt(8_000_000 / (viewport.width * viewport.height)),
        8192 / viewport.width, 8192 / viewport.height);
    const canvas = document.createElement('canvas');
    canvas.setAttribute('aria-hidden', 'true');
    canvas.width = Math.max(1, Math.floor(viewport.width * ratio));
    canvas.height = Math.max(1, Math.floor(viewport.height * ratio));
    canvas.style.width = `${viewport.width}px`; canvas.style.height = `${viewport.height}px`;
    const text = document.createElement('div');
    text.className = 'pdf-text-layer';
    text.style.setProperty('--total-scale-factor', viewport.scale * viewport.userUnit);
    state.renderTask = page.render({ canvas, viewport, transform: [ratio, 0, 0, ratio, 0, 0] });
    state.textLayer = new state.pdfjs.TextLayer({ textContentSource: page.streamTextContent(), container: text, viewport });
    const results = await Promise.allSettled([state.renderTask.promise, state.textLayer.render()]);
    if (state.disposed || sequence !== state.sequence) { canvas.width = canvas.height = 0; return; }
    const failed = results.find(result => result.status === 'rejected');
    if (failed) { canvas.width = canvas.height = 0; throw failed.reason; }
    const previous = state.page;
    for (const old of state.host.querySelectorAll('canvas')) old.width = old.height = 0;
    state.host.style.width = `${viewport.width}px`; state.host.style.height = `${viewport.height}px`;
    state.host.replaceChildren(canvas, text);
    state.page = page;
    state.actualZoom = scale * 72 / 96 * 100;
    if (previous && previous !== page) previous.cleanup();
}

export function getZoom(host) { return viewers.get(host)?.actualZoom ?? 100; }

export async function dispose(host) {
    const state = viewers.get(host);
    if (!state) return;
    viewers.delete(host);
    state.disposed = true; ++state.sequence;
    state.observer?.disconnect();
    state.renderTask?.cancel(); state.textLayer?.cancel();
    await state.loading?.destroy();
    await state.pending?.catch(() => {});
    for (const canvas of host.querySelectorAll('canvas')) canvas.width = canvas.height = 0;
    host.replaceChildren();
}
