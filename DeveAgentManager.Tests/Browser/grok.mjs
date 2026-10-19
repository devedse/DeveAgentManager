// Run against a disposable Manager. Exercises the real settings, account and session APIs.
import assert from 'node:assert/strict';
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
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
        await page.locator('[name=newPassword]').fill('test-only-grok-browser-password');
        await page.locator('[name=confirmPassword]').fill('test-only-grok-browser-password');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    }
    const info = await (await context.request.get(base + '/api/info')).json();
    const grok = info.harnesses.find(h => h.id === 'grok');
    assert.ok(grok.hasSessionFormat && grok.hasModelCatalog && grok.accountSupport);
    await page.goto(base + '/manager-settings');
    await page.getByRole('tab', { name: 'Grok', exact: true }).click();
    const panel = page.getByRole('tabpanel').filter({ has: page.getByText('Grok settings', { exact: true }) });
    const model = panel.getByRole('combobox', { name: 'Model', exact: true });
    await model.fill('test-only-grok-model'); await model.press('Tab');
    await panel.locator('.thinking-select').click();
    await page.getByRole('option', { name: 'high', exact: true }).click();
    const saved = page.waitForResponse(r => new URL(r.url()).pathname === '/api/settings/profile' && r.request().method() === 'PUT');
    await panel.getByRole('button', { name: 'Save and apply to Runners', exact: true }).click();
    assert.equal((await saved).status(), 200);
    await page.reload();
    await page.getByRole('tab', { name: 'Grok', exact: true }).click();
    await model.waitFor(); assert.equal(await model.inputValue(), 'test-only-grok-model');
    assert.match(await panel.locator('.thinking-select').textContent(), /high/);
    console.log('Grok model and effort persist through the real settings API.');

    await page.goto(base + '/harness-logins');
    const accounts = page.locator('.settings-section').filter({ has: page.locator('.section-title').getByText('Grok', { exact: true }) });
    await accounts.getByRole('button', { name: 'Connect login', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByText('API key', { exact: true }).click();
    await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill('Test-only Grok login');
    await dialog.getByLabel('XAI_API_KEY', { exact: true }).fill('xai-test-only-browser-key');
    const connected = page.waitForResponse(r => new URL(r.url()).pathname.replace(/\/$/, '') === '/api/accounts' && r.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    assert.equal((await connected).status(), 200);
    await accounts.locator('.account-name').filter({ hasText: 'Test-only Grok login' }).first().waitFor();
    const response = await context.request.get(base + '/api/accounts');
    const accountJson = await response.text(); assert.ok(!accountJson.includes('xai-test-only-browser-key'));
    await page.reload(); await accounts.locator('.account-name').filter({ hasText: 'Test-only Grok login' }).first().waitFor();
    console.log('Grok API-key login persists and its secret is excluded from account responses.');

    await page.goto(base + '/sessions');
    const history = { format: 'deve-session', version: 1, title: 'Grok browser conversation', createdAt: '2026-10-19T00:00:00Z', updatedAt: '2026-10-19T00:00:01Z',
        sources: [{ harness: 'grok', sessionId: '0199f000-0000-7000-8000-000000000001' }], leafId: 'a', entries: [
            { id: 'u', kind: 'message', role: 'user', content: [{ type: 'text', text: 'Test-only user message' }] },
            { id: 'a', parentId: 'u', kind: 'message', role: 'assistant', content: [{ type: 'text', text: 'Test-only Grok reply' }] }
        ] };
    const imported = page.waitForResponse(r => new URL(r.url()).pathname === '/api/sessions/import');
    await page.locator('input[type=file]').setInputFiles({ name: 'grok-test.deve.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(history)) });
    const result = await imported; assert.equal(result.status(), 200);
    const session = await result.json();
    await page.goto(base + '/sessions/' + session.id);
    await page.getByText('Test-only Grok reply', { exact: true }).waitFor();
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Export', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Grok session', exact: true }).click();
    assert.match((await download).suggestedFilename(), /\.grok\.json$/);
    const exported = await context.request.get(base + `/api/sessions/${session.id}/export?format=grok`);
    assert.equal(exported.status(), 200);
    const bundle = await exported.json(); assert.equal(bundle.format, 'grok-session'); assert.ok(bundle.files['summary.json']);
    console.log('Grok conversations render and export a native bundle through the browser.');
    for (const width of [390, 1280]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + '/manager-settings');
        await page.getByRole('tab', { name: 'Grok', exact: true }).click();
        await model.waitFor(); assert.equal(await model.inputValue(), 'test-only-grok-model');
    }
    assert.deepEqual(errors, []);
} finally { await browser.close(); }
