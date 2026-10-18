// Real HTTP, authorization and sandbox checks; its disposable Manager data directory is supplied by the CI wrapper.
import assert from 'node:assert/strict';
import { randomUUID, createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import http from 'node:http';
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const browser = await playwright[process.env.DAM_TEST_BROWSER ?? 'chromium'].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
assert.ok(process.env.DAM_TEST_DATA_DIR, 'Use the isolated browser wrapper, or supply its disposable DAM_TEST_DATA_DIR');
const dataDirectory = resolve(process.env.DAM_TEST_DATA_DIR);
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII=', 'base64');
let session, context;
const external = http.createServer((req, res) => { res.setHeader('Content-Type', 'image/png'); res.end(png); });
await new Promise(resolve => external.listen(0, '127.0.0.1', resolve));
try {
    context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const imported = await context.request.post(base + '/api/sessions/import', {
        headers: { 'X-DAM-Request': '1', Origin: base },
        multipart: { file: { name: 'html-preview.deve.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ format: 'deve-session', version: 1, title: 'HTML preview', sources: [], entries: [] })) } }
    });
    assert.equal(imported.status(), 200, await imported.text());
    session = (await imported.json()).id;
    async function share(name, content) {
        const id = randomUUID(), bytes = Buffer.isBuffer(content) ? content : Buffer.from(content);
        const folder = join(dataDirectory, 'sessions', 'files', session.replaceAll('-', ''));
        await mkdir(folder, { recursive: true });
        await writeFile(join(folder, id.replaceAll('-', '') + '.file'), bytes);
        await writeFile(join(folder, id.replaceAll('-', '') + '.json'), JSON.stringify({ id, sessionId: session, fileName: name, caption: '', bytes: bytes.length,
            sha256: createHash('sha256').update(bytes).digest('hex'), createdAt: new Date().toISOString() }));
        return id;
    }
    const html = `<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="assets/site.css"></head><body>
        <h1>HTML website preview</h1><img id="embedded" src="data:image/png;base64,${png.toString('base64')}">
        <img id="relative" src="assets/picture.png"><img id="external" src="http://127.0.0.1:${external.address().port}/picture.png">
        <button id="interactive" onclick="document.querySelector('#result').textContent='Clicked'">Try it</button><div id="result"></div>
        <script src="assets/site.js"></script><script>
        try { window.parent.document.body.dataset.compromised='yes'; } catch { document.body.dataset.parentBlocked='yes'; }
        try { document.cookie; } catch { document.body.dataset.cookieBlocked='yes'; }
        try { localStorage.getItem('anything'); } catch { document.body.dataset.storageBlocked='yes'; }
        fetch('/api/me', {credentials:'include'}).then(r=>r.text()).then(()=>document.body.dataset.apiReadable='yes').catch(()=>document.body.dataset.apiBlocked='yes');
        fetch('assets/config.json').then(r=>r.json()).then(value=>document.body.dataset.assetValue=value.message);
        </script><!--${'large HTML content '.repeat(600000)}--></body></html>`;
    const id = await share('website.html', html);
    await share('site.css', 'h1 { color: rgb(0, 128, 0); } body { margin: 12px; }');
    await share('site.js', "document.body.dataset.scriptLoaded='yes'");
    await share('config.json', '{"message":"scoped asset loaded"}');
    await share('picture.png', png);
    const largeText = 'A long log file\n'.repeat(100000);
    await share('large.log', largeText);
    await share('large.md', '# Large Markdown\n\n' + 'Readable content.\n\n'.repeat(40000));
    const assetCookies = [];
    page.on('request', request => { if (request.url().includes('/view/assets/')) assetCookies.push(request.allHeaders().then(headers => headers.cookie)); });
    await page.goto(base + '/sessions/' + session);
    await page.getByRole('button', { name: 'View website.html', exact: true }).click();
    console.log('Checking HTML rendering and asset loading');
    const dialog = page.locator('.file-preview-dialog');
    const frame = page.frameLocator('iframe[title="HTML preview of website.html"]');
    await frame.getByRole('heading', { name: 'HTML website preview' }).waitFor({ timeout: 30000 });
    await frame.locator('body[data-script-loaded=yes][data-parent-blocked=yes][data-cookie-blocked=yes][data-storage-blocked=yes][data-api-blocked=yes]').waitFor();
    await frame.locator('body[data-asset-value="scoped asset loaded"]').waitFor();
    assert.equal(await frame.locator('h1').evaluate(e => getComputedStyle(e).color), 'rgb(0, 128, 0)');
    for (const name of ['embedded', 'relative', 'external']) assert.equal(await frame.locator('#' + name).evaluate(e => e.complete && e.naturalWidth > 0), true, name);
    await frame.getByRole('button', { name: 'Try it' }).click();
    await frame.getByText('Clicked', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.body.dataset.compromised), undefined);
    assert.ok(assetCookies.length >= 3 && (await Promise.all(assetCookies)).every(cookie => !cookie), 'Sandbox asset requests must work without Portal cookies');
    for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 850 });
        await page.waitForTimeout(400);
        const overflow = await page.evaluate(() => [...document.querySelectorAll('body *')]
            .filter(e => getComputedStyle(e).display !== 'none' && (e.getBoundingClientRect().right > innerWidth + 1 || e.getBoundingClientRect().left < -1))
            .map(e => ({ tag: e.tagName, css: e.className, width: e.getBoundingClientRect().width })).slice(0, 15));
        await page.screenshot({ path: `/tmp/dam-html-viewer-${width}.png` });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}: ${JSON.stringify(overflow)}`);
    }
    await dialog.getByRole('button', { name: 'Text', exact: true }).click();
    console.log('Checking streamed HTML source');
    const sourceFrame = page.frameLocator('iframe[title="Source of website.html"]');
    await sourceFrame.locator('pre').waitFor();
    assert.equal(await sourceFrame.locator('pre').evaluate(e => e.textContent.startsWith('<!doctype html>')), true);
    assert.equal(await sourceFrame.locator('script').count(), 0);
    await dialog.getByRole('button', { name: 'Rendered', exact: true }).click();
    console.log('Checking isolated new-tab view');
    const popup = await Promise.all([page.waitForEvent('popup'), dialog.getByRole('link', { name: 'Open in new tab', exact: true }).click()]).then(values => values[0]);
    await popup.getByRole('heading', { name: 'HTML website preview' }).waitFor();
    assert.equal(await popup.evaluate(() => window.opener), null);
    assert.equal(await popup.locator('body').getAttribute('data-cookie-blocked'), 'yes');
    await popup.close();
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    console.log('Checking large text and Markdown');
    await page.getByRole('button', { name: 'View large.log', exact: true }).click();
    await page.frameLocator('iframe[title="Source of large.log"]').locator('pre').waitFor();
    assert.equal(await page.frameLocator('iframe[title="Source of large.log"]').locator('pre').evaluate(e => e.textContent.startsWith('A long log file')), true);
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.getByRole('button', { name: 'View large.md', exact: true }).click();
    await page.locator('iframe[title="Source of large.md"]').waitFor();
    await dialog.getByRole('button', { name: 'Rendered', exact: true }).click();
    await dialog.getByRole('heading', { name: 'Large Markdown', exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.body.dataset.compromised), undefined);
    assert.deepEqual(errors, []);
    console.log('Passed: 10 MiB HTML streams with embedded/external/relative images, CSS, JS and JSON; source/new-tab views; parent/cookie/API isolation; large text/Markdown; phone layout.');
} finally {
    if (session && context) await context.request.delete(base + '/api/sessions/' + session, { headers: { 'X-DAM-Request': '1', Origin: base } });
    await browser.close(); external.close();
}
