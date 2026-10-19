// Run against a disposable Manager. Uses the real settings API except for deliberate loading/save failures.
import assert from 'node:assert/strict';
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const endpoint = '/api/settings/history-sync';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
    await Promise.all([page.waitForURL(url => url.pathname !== '/login'), page.locator('button[type=submit]').click()]);
    if (await page.locator('[name=currentPassword]').isVisible()) {
        await page.locator('[name=currentPassword]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
        await page.locator('[name=newPassword]').fill('test-only-history-browser-new-password');
        await page.locator('[name=confirmPassword]').fill('test-only-history-browser-new-password');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    }
    const section = page.locator('[data-section=history-sync-settings]');
    const select = section.locator('.mud-select').first();
    const save = section.getByRole('button', { name: 'Save conversation sync', exact: true });
    const currentMode = async () => (await (await context.request.get(base + endpoint)).json()).mode;
    const choose = async label => {
        await select.click();
        await page.getByRole('option', { name: label, exact: true }).click();
    };
    const persist = async () => {
        const response = page.waitForResponse(r => new URL(r.url()).pathname === endpoint && r.request().method() === 'PUT');
        await save.click();
        assert.equal((await response).status(), 200);
        await page.getByText('Conversation sync saved. Applies to the next synchronization pass.', { exact: true }).last().waitFor();
    };
    await page.goto(base + '/manager-settings?tab=diagnostics');
    await select.waitFor();
    assert.match(await select.textContent(), /Sampled \(recommended\)/);
    assert.equal(await save.isDisabled(), true);
    await choose('Shadow'); await persist();
    assert.equal(await currentMode(), 'Shadow');
    await choose('Full'); await persist();
    await page.reload();
    await select.waitFor();
    assert.match(await select.textContent(), /Full/);
    console.log('Saved modes persist after reload.');

    let slow = true, failLoad = false, failSave = false;
    await context.route('**/api/settings/history-sync', async route => {
        if (route.request().method() === 'GET' && slow) await new Promise(resolve => setTimeout(resolve, 1800));
        if (route.request().method() === 'GET' && failLoad || route.request().method() === 'PUT' && failSave)
            return route.fulfill({ status: 500, json: { title: 'Test-only settings failure', detail: 'Test-only settings failure' } });
        return route.continue();
    });
    await page.reload();
    await section.locator('[aria-busy=true]').waitFor();
    assert.equal(await select.count(), 0, 'no default mode is displayed before settings load');
    await select.waitFor();
    assert.match(await select.textContent(), /Full/);
    slow = false; failLoad = true;
    await page.reload();
    await section.getByRole('alert').waitFor();
    assert.equal(await select.count(), 0);
    failLoad = false;
    await section.getByRole('button', { name: 'Retry', exact: true }).click();
    await select.waitFor();
    assert.match(await select.textContent(), /Full/);
    failSave = true;
    console.log('Slow loads, failed loads and retry preserve the saved mode.');
    await choose('Shadow'); await save.click();
    await section.getByRole('alert').waitFor();
    assert.equal(await currentMode(), 'Full', 'failed save keeps the persisted choice');
    failSave = false;
    await persist();
    assert.equal(await currentMode(), 'Shadow');

    for (const theme of ['light', 'dark']) {
        await page.addInitScript(value => localStorage.setItem('dam-theme', value), theme);
        for (const width of [390, 320]) {
            await page.setViewportSize({ width, height: 850 });
            await page.reload(); await select.waitFor();
            assert.match(await select.textContent(), /Shadow/);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}px ${theme} overflow`);
            await section.scrollIntoViewIfNeeded();
            await page.screenshot({ path: `/tmp/dam-history-sync-settings-${theme}-${width}.png` });
        }
    }
    await section.getByRole('button', { name: 'Help about Conversation sync', exact: true }).click();
    await page.getByText(/An edit in the middle can remain undetected until a full check/).waitFor();
    assert.deepEqual(errors, []);
    console.log('Passed: history verification saves/reloads, slow/error/retry states, failed saves, help and phone layouts in both themes.');
} finally { await browser.close(); }
