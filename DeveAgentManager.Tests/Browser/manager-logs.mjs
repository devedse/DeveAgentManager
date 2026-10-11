const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Text selected in xterm.js gets a Copy button where the selection ended: Shift+drag where tmux has the mouse (a plain
// drag still goes to tmux), a plain drag on a read-only terminal. The button and Ctrl+C copy, say so, and keep the
// terminal focused.
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
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): phone logs page searches, refreshes while paused and exports without overflow.`);
} finally { await browser.close(); }
