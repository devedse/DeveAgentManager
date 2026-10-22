// Real PDF rendering, range requests and access-checked shared-file URLs on a disposable Manager.
import assert from 'node:assert/strict';
import { randomUUID, createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
const { chromium } = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const browser = await chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
assert.ok(process.env.DAM_TEST_DATA_DIR, 'Use the isolated browser wrapper with its disposable data directory');

function pdf(padding = 0) {
    const objects = [null,
        '<< /Type /Catalog /Pages 2 0 R /OpenAction 10 0 R >>',
        '<< /Type /Pages /Count 3 /Kids [4 0 R 6 0 R 8 0 R] >>',
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'];
    for (let number = 1; number <= 3; number++) {
        const content = `0.1 0.5 0.8 rg 72 480 300 140 re f 0 0 0 rg BT /F1 24 Tf 72 720 Td (PDF preview page ${number}) Tj ET`;
        objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents ${objects.length + 1} 0 R >>`,
            `<< /Length ${Buffer.byteLength(content)} >>\nstream\n${content}\nendstream`);
    }
    objects.push('<< /Type /Action /S /JavaScript /JS (globalThis.pdfScriptExecuted = true;) >>');
    let source = '%PDF-1.7\n%\x80\x81\x82\x83\n';
    const offsets = [0];
    for (let number = 1; number < objects.length; number++) {
        offsets.push(Buffer.byteLength(source, 'latin1'));
        source += `${number} 0 obj\n${objects[number]}\nendobj\n`;
    }
    if (padding) source += '%' + ' '.repeat(padding) + '\n';
    const start = Buffer.byteLength(source, 'latin1');
    source += `xref\n0 ${objects.length}\n0000000000 65535 f \n`;
    source += offsets.slice(1).map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
    source += `trailer\n<< /Size ${objects.length} /Root 1 0 R >>\nstartxref\n${start}\n%%EOF\n`;
    return Buffer.from(source, 'latin1');
}

try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
    const page = await context.newPage();
    page.setDefaultTimeout(20000);
    const errors = [], requests = [], ranges = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => requests.push(request.url()));
    page.on('response', response => { if (response.status() === 206 && response.url().includes('/shared-preview/')) ranges.push(response.url()); });
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const imported = await context.request.post(base + '/api/sessions/import', {
        headers: { 'X-DAM-Request': '1', Origin: base },
        multipart: { file: { name: 'pdf-preview.deve.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ format: 'deve-session', version: 1, title: 'PDF preview', sources: [], entries: [] })) } }
    });
    assert.equal(imported.status(), 200, await imported.text());
    const session = (await imported.json()).id;
    async function share(name, bytes) {
        const id = randomUUID(), folder = join(resolve(process.env.DAM_TEST_DATA_DIR), 'sessions', 'files', session.replaceAll('-', ''));
        await mkdir(folder, { recursive: true });
        await writeFile(join(folder, id.replaceAll('-', '') + '.file'), bytes);
        await writeFile(join(folder, id.replaceAll('-', '') + '.json'), JSON.stringify({ id, sessionId: session, fileName: name, caption: '', bytes: bytes.length,
            sha256: createHash('sha256').update(bytes).digest('hex'), createdAt: new Date().toISOString() }));
        return id;
    }
    const document = pdf() as Buffer<ArrayBuffer> & Record<string, any>, large = pdf(16 * 1024 * 1024);
    const largeId = await share('large-design.PDF', large);
    await share('design.pdf', document);
    await share('slow.pdf', document);
    const invalid = Buffer.from('%PDF-1.7\ninvalid test-only document');
    await share('invalid.pdf', invalid);
    let failRead = false, gate = null;
    await context.route('**/shared-preview/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path.endsWith('/slow.pdf') && gate) await gate;
        if (path.endsWith('/design.pdf') && failRead) return route.fulfill({ status: 503, body: 'Test-only PDF read failure' });
        await route.continue();
    });
    const dialog = page.locator('.file-preview-dialog');
    const contents = dialog.locator('.pdf-text-layer');
    const ready = async number => {
        await contents.getByText(`PDF preview page ${number}`, { exact: true }).waitFor();
        await dialog.locator('.pdf-preview-stage[aria-busy=false]').waitFor();
    };
    const close = async () => {
        await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
        await dialog.waitFor({ state: 'hidden' });
        for (let i = 0; i < 50 && page.workers().length; i++) await new Promise(resolve => setTimeout(resolve, 100));
        assert.equal(page.workers().length, 0, 'closing a PDF releases its parser worker');
    };
    for (const theme of ['dark', 'light'] as const) {
        await page.emulateMedia({ colorScheme: theme });
        await page.evaluate(theme => localStorage.setItem('dam-theme', theme), theme);
        for (const width of [1280, 390, 320]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(base + '/sessions/' + session);
            await page.getByRole('button', { name: 'View design.pdf', exact: true }).click();
            await ready(1);
            assert.equal(await dialog.getByRole('button', { name: 'Previous page', exact: true }).isDisabled(), true);
            assert.equal(await dialog.locator('.pdf-page-count').innerText(), 'of 3');
            const canvas = dialog.locator('canvas');
            assert.equal(await canvas.count(), 1);
            assert.ok(await canvas.evaluate((canvas: HTMLCanvasElement) => {
                const color = canvas.getContext('2d').getImageData(Math.floor(canvas.width * .2), Math.floor(canvas.height * .3), 1, 1).data;
                return color[2] > color[0] && color[2] > 100;
            }), 'PDF graphics are rendered, not just a text extraction');
            const selected = await contents.evaluate(layer => {
                const selection = getSelection(), range = document.createRange();
                range.selectNodeContents(layer); selection.removeAllRanges(); selection.addRange(range);
                const value = selection.toString(); selection.removeAllRanges(); return value;
            });
            assert.match(selected, /PDF preview page 1/);
            await dialog.getByRole('button', { name: 'Next page', exact: true }).click();
            await ready(2);
            await dialog.getByRole('spinbutton', { name: 'Page', exact: true }).fill('3');
            await dialog.getByRole('spinbutton', { name: 'Page', exact: true }).press('Enter');
            await ready(3);
            assert.equal(await dialog.getByRole('button', { name: 'Next page', exact: true }).isDisabled(), true);
            await dialog.getByRole('button', { name: 'Previous page', exact: true }).click();
            await ready(2);
            const before = (await canvas.boundingBox()).width;
            await dialog.getByRole('button', { name: 'Zoom in', exact: true }).click();
            await ready(2);
            assert.ok((await canvas.boundingBox()).width > before, 'zoom enlarges the rendered page');
            await dialog.getByRole('button', { name: 'Zoom out', exact: true }).click();
            await ready(2);
            await dialog.getByRole('button', { name: 'Fit width', exact: true }).click();
            await ready(2);
            assert.equal(await dialog.locator('.pdf-zoom-label').innerText(), 'Fit');
            assert.ok(Math.abs((await canvas.boundingBox()).width - before) < 2);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `PDF view fits ${width}px/${theme}`);
            const bounds = await dialog.boundingBox();
            assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width + 1);
            await page.screenshot({ path: `/tmp/dam-pdf-viewer-${theme}-${width}.png` });
            await close();
        }
    }
    // Fit-to-width follows viewport changes while a document remains open.
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base + '/sessions/' + session);
    await page.getByRole('button', { name: 'View design.pdf', exact: true }).click();
    await ready(1);
    await page.setViewportSize({ width: 320, height: 900 });
    await page.waitForFunction(() => {
        const scroll = document.querySelector('.pdf-preview-scroll'), canvas = scroll?.querySelector('canvas');
        return canvas && canvas.getBoundingClientRect().width <= scroll.clientWidth - 23;
    });
    await close();
    const descriptor = await context.request.get(base + `/api/sessions/${session}/files/${largeId}/preview`);
    assert.ok(Buffer.byteLength(await descriptor.text()) < 4096, 'large PDFs return a small descriptor');
    await page.getByRole('button', { name: 'View large-design.PDF', exact: true }).click();
    await ready(1);
    assert.ok(ranges.length > 0, 'large PDFs use real HTTP range requests');
    assert.equal(await dialog.locator('canvas').count(), 1, 'only the selected page is rendered');
    const [download] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('link', { name: 'Download', exact: true }).click()]);
    assert.equal(download.suggestedFilename(), 'large-design.PDF');
    await close();
    await page.getByRole('button', { name: 'View invalid.pdf', exact: true }).click();
    await dialog.getByText("Couldn't load this PDF. Try again or download the original file.", { exact: true }).waitFor();
    await dialog.getByRole('link', { name: 'Download', exact: true }).waitFor();
    await close();
    failRead = true;
    await page.getByRole('button', { name: 'View design.pdf', exact: true }).click();
    await dialog.getByRole('button', { name: 'Retry', exact: true }).waitFor();
    failRead = false;
    await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
    await ready(1);
    await close();
    let release;
    gate = new Promise(resolve => { release = resolve; });
    try {
        await Promise.all([page.waitForRequest(request => new URL(request.url()).pathname.endsWith('/slow.pdf')),
            page.getByRole('button', { name: 'View slow.pdf', exact: true }).click()]);
        await dialog.locator('[role=status][aria-busy=true]', { hasText: 'Loading PDF' }).waitFor();
        assert.equal(await dialog.locator('.pdf-page-count').count(), 0, 'page count is not shown before loading');
        await close();
    } finally { gate = null; release(); }
    assert.equal(await page.evaluate(() => globalThis.pdfScriptExecuted), undefined, 'PDF document scripts are never executed');
    assert.ok(requests.filter(url => /^https?:/.test(url)).every(url => new URL(url).origin === base), 'PDFs and rendering assets stay local');
    assert.deepEqual(errors, []);
    console.log('Passed: PDF graphics and selectable text, page navigation, zoom/fit, resize, 16 MiB ranged reads, downloads, invalid/network failure and retry, close-during-load cleanup, phones and both themes.');
} finally { await browser.close(); }
