const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

// Manager log filtering, phone layout, export and clipboard copies beyond the displayed 300 entries.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch(engine === 'firefox' ? { firefoxUserPrefs: { 'dom.events.testing.asyncClipboard': true } } : {});
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...(engine === 'chromium' ? { permissions: ['clipboard-read', 'clipboard-write'] } : {}) });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(base + '/logs');
    await page.getByText('Manager logs', { exact: true }).last().waitFor({ timeout: 60000 });
    await page.locator('.manager-log-entry').first().waitFor();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.getByLabel('Search message or category').fill('Application started');
    await page.waitForTimeout(1200);
    assert.ok(await page.locator('.manager-log-entry').count() > 0);
    assert.match(await page.locator('.manager-logs').textContent(), /Application started/);
    await page.getByLabel('Auto refresh').uncheck();
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download', exact: true }).click();
    assert.equal((await download).suggestedFilename(), 'manager-logs.txt');
    const requests = [];
    await context.route('**/api/logs?*', route => {
        const url = new URL(route.request().url());
        requests.push(url.searchParams);
        const count = Number(url.searchParams.get('take'));
        return route.fulfill({ json: Array.from({ length: count }, (_, i) => ({ id: 1200 - i, timestamp: '2026-10-11T12:00:00Z', level: 'Information', category: 'Test', message: `Application started ${1200 - i}` })) });
    });
    for (const count of [100, 1000]) {
        await page.getByRole('button', { name: count === 100 ? 'Copy last 100' : 'Copy last 1,000', exact: true }).click();
        await page.getByText(`Copied ${count.toLocaleString('en-US')} log entries`, { exact: true }).waitFor();
        const copied = await page.evaluate(() => navigator.clipboard.readText());
        assert.equal((copied.match(/\[Information\]/g) ?? []).length, count);
        assert.match(copied, /Application started 1200/);
        assert.ok(copied.endsWith(`Application started ${1201 - count}`));
        assert.equal(requests.at(-1).get('search'), 'Application started');
        assert.equal(requests.at(-1).get('minimum'), 'Information');
    }
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): phone logs page filters, exports and copies the latest 100/1,000 matching entries without overflow.`);
} finally { await browser.close(); }
