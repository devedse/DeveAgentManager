const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000010', now = new Date().toISOString();
    const files = ['report.md', 'server.log', 'app.apk', 'large.txt'].map((fileName, i) => ({ id: `00000000-0000-0000-0000-00000000002${i}`, sessionId: id, fileName, caption: '', bytes: i === 3 ? 900000 : 200, sha256: 'a'.repeat(64), createdAt: now }));
    const markdown = '# Results\n\n| Case | Result |\n| --- | --- |\n| Sample | Passed |\n\n- [x] Verified\n\n```text\n<script>window.untrusted=true</script>\n```\n\n<script>window.untrusted=true</script>\n\n[Bad](javascript:alert(1))\n[Report](https://example.com/report#details)\n';
    let fail = false, delay = false;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        const file = files.find(file => path.startsWith(`/api/sessions/${id}/files/${file.id}`));
        if (file && path.endsWith('/preview')) {
            if (delay) await new Promise(resolve => setTimeout(resolve, 1100));
            if (fail) return route.fulfill({ status: 503, json: { detail: 'Test-only preview unavailable' } });
            return route.fulfill({ json: file === files[0] ? { text: markdown, markdown: true } : file === files[1] ? { text: 'line 1\n\tline 2 <tag>\n' + 'long'.repeat(150), markdown: false }
                : file === files[2] ? { text: null, markdown: false, unavailableReason: 'This file contains binary data. Download it to open it.' }
                : { text: 'large text content\n'.repeat(50000), markdown: false } });
        }
        if (file && path.endsWith('/download')) return route.fulfill({ body: 'test-only original content', headers: { 'content-disposition': `attachment; filename="${file.fileName}"` } });
        const body = path === '/api/info' ? info : path === `/api/sessions/${id}` ? { id, access: 'Read', ownerName: 'Test user', title: 'Files', harnessId: 'codex', messageCount: 0, createdAt: now, updatedAt: now }
            : path === `/api/sessions/${id}/files` ? files : path.endsWith('/content') ? { format: 'deve-session', version: 1, entries: [], sources: [] } : [];
        return route.fulfill({ json: body });
    });
    for (const theme of ['dark', 'light']) {
        await page.emulateMedia({ colorScheme: theme });
        await page.evaluate(theme => localStorage.setItem('dam-theme', theme), theme);
        for (const width of [1280, 390, 320]) {
            await page.setViewportSize({ width, height: 850 });
            await page.goto(base + `/sessions/${id}`);
            await page.getByRole('button', { name: 'View report.md', exact: true }).click();
            const dialog = page.locator('.file-preview-dialog');
            await dialog.getByRole('heading', { name: 'Results', exact: true }).waitFor();
            assert.equal(await dialog.locator('table').count(), 1);
            assert.equal(await dialog.locator('script').count(), 0);
            assert.equal(await dialog.locator('a[href^="javascript:"]').count(), 0);
            assert.equal(await page.evaluate(() => window.untrusted), undefined);
            await dialog.getByRole('button', { name: 'Text', exact: true }).click();
            assert.equal(await dialog.locator('.file-preview-text').textContent(), markdown);
            assert.equal(await dialog.locator('.file-preview-text script').count(), 0);
            await dialog.getByRole('button', { name: 'Rendered', exact: true }).click();
            await dialog.locator('table').waitFor();
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `page overflows ${width}/${theme}`);
            const box = await dialog.boundingBox();
            assert.ok(box.x >= 0 && box.x + box.width <= width + 1, 'viewer escapes phone');
            await page.screenshot({ path: `/tmp/dam-file-viewer-${theme}-${width}.png` });
            await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
        }
    }
    assert.equal(await page.getByRole('button', { name: /^Delete / }).count(), 0);
    await page.getByRole('button', { name: 'View server.log', exact: true }).click();
    const dialog = page.locator('.file-preview-dialog');
    await dialog.locator('.file-preview-text').waitFor();
    assert.match(await dialog.locator('.file-preview-text').textContent(), /^line 1\n\tline 2 <tag>/);
    assert.equal(await dialog.getByRole('button', { name: 'Rendered', exact: true }).count(), 0);
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.getByRole('button', { name: 'View large.txt', exact: true }).click();
    await dialog.locator('.file-preview-text').waitFor();
    assert.ok((await dialog.locator('.file-preview-text').textContent()).length > 512 * 1024);
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    for (const name of ['app.apk']) {
        await page.getByRole('button', { name: `View ${name}`, exact: true }).click();
        await dialog.getByText(/contains binary data/).waitFor();
        const [download] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('link', { name: 'Download', exact: true }).click()]);
        assert.equal(download.suggestedFilename(), name);
        await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    }
    fail = true; delay = true;
    await page.getByRole('button', { name: 'View report.md', exact: true }).click();
    await dialog.locator('[aria-busy=true]').waitFor();
    assert.equal(await dialog.locator('.file-preview-text, article').count(), 0);
    await dialog.getByRole('button', { name: 'Retry', exact: true }).waitFor();
    fail = false; delay = false;
    await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
    await dialog.getByRole('heading', { name: 'Results', exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): Markdown/text toggle, tables/code, inert content, original downloads, binary fallback, large text, read-only access, loading/retry, 320px phone and both themes.`);
} finally { await browser.close(); }
