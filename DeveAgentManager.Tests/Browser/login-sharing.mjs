import assert from 'node:assert/strict';
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5197';
const password = process.env.DAM_TEST_PASSWORD ?? 'preview-test-only';
const browser = await playwright[engine].launch();
const suffix = engine.slice(0,2) + Date.now().toString(36);
const errors = [];
async function login(username, initial, change = false) {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
    const page = await context.newPage();
    page.setDefaultTimeout(60000);
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login', { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="username"]').fill(username);
    await page.locator('input[name="password"]').fill(initial);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/login')), page.locator('button[type="submit"]').click()]);
    if (change) {
        await page.locator('input[name="currentPassword"]').fill(initial);
        await page.locator('input[name="newPassword"]').fill(initial + '-changed');
        await page.locator('input[name="confirmPassword"]').fill(initial + '-changed');
        await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/password')), page.getByRole('button', { name: 'Change password', exact: true }).click()]);
    }
    // Wait for the landing page's WASM boot before navigating away. Firefox reports aborted
    // assembly downloads as page errors if the test immediately replaces the prerendered page.
    await page.waitForLoadState('networkidle');
    return { context, page };
}
async function api(context, method, path, data) {
    return context.request.fetch(base + '/api/' + path, { method, data, headers: { Origin: base, 'X-DAM-Request': '1' } });
}
try {
    const admin = await login('admin', password);
    const recipientName = 'recipient-' + suffix;
    const managerName = 'manager-' + suffix;
    const created = await api(admin.context, 'POST', 'users', { username: recipientName, password, roles: 'None' });
    assert.ok(created.ok(), await created.text());
    const recipient = await created.json();
    const managerResponse = await api(admin.context, 'POST', 'users', { username: managerName, password, roles: 'HarnessLoginManager' });
    assert.ok(managerResponse.ok(), await managerResponse.text());
    const manager = await managerResponse.json();
    const added = await api(admin.context, 'POST', 'accounts', { harnessId: 'claude', name: 'Team ' + suffix, kind: 'Subscription', secret: 'test-only-placeholder-token' });
    assert.ok(added.ok(), await added.text());
    const account = await added.json();
    await admin.page.goto(base + '/harness-logins', { waitUntil: 'domcontentloaded' });
    const row = admin.page.locator('.account-row').filter({ hasText: account.name });
    await row.getByRole('button', { name: 'Manage access', exact: true }).click();
    await admin.page.getByRole('button', { name: 'Add user', exact: true }).click();
    await admin.page.getByLabel('Person', { exact: true }).click();
    await admin.page.getByRole('option').filter({ hasText: recipientName }).click();
    await admin.page.getByRole('button', { name: 'Add', exact: true }).click();
    await admin.page.getByText(recipientName + ' (' + recipientName + ')', { exact: true }).waitFor();
    await admin.page.getByRole('button', { name: 'Done', exact: true }).click();
    assert.equal(await admin.page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    const user = await login(recipientName, password, true);
    await user.page.goto(base + '/settings', { waitUntil: 'domcontentloaded' });
    await user.page.getByRole('tab', { name: 'Harness logins' }).click();
    const shared = user.page.locator('.account-row').filter({ hasText: account.name });
    await shared.waitFor();
    assert.equal(await shared.getByRole('button', { name: 'Manage access', exact: true }).count(), 0);
    await shared.getByRole('button', { name: 'Make ' + account.name + ' my default', exact: true }).click();
    await shared.getByText('your default', { exact: true }).waitFor();
    assert.equal((await api(user.context, 'PUT', 'accounts/' + account.id + '/secret', { kind: 'Subscription', secret: 'test-only-denied-token' })).status(), 403);
    assert.equal((await api(user.context, 'GET', 'accounts/all')).status(), 403);
    assert.equal((await api(user.context, 'GET', 'accounts/' + account.id + '/grants')).status(), 403);
    const global = await login(managerName, password, true);
    assert.equal((await api(global.context, 'GET', 'accounts')).status(), 200);
    assert.equal((await (await api(global.context, 'GET', 'accounts')).json()).length, 0);
    await global.page.goto(base + '/harness-logins', { waitUntil: 'domcontentloaded' });
    const globalRow = global.page.locator('.account-row').filter({ hasText: account.name });
    await globalRow.getByText(/Administration only/).waitFor();
    await globalRow.getByRole('button', { name: 'Manage access', exact: true }).click();
    await global.page.getByLabel('Access for ' + recipientName, { exact: true }).click();
    await global.page.getByRole('menuitem', { name: 'Change to Manage' }).click();
    await global.page.getByRole('button', { name: 'Done', exact: true }).click();
    await user.page.reload({ waitUntil: 'domcontentloaded' });
    await user.page.waitForLoadState('networkidle');
    await user.page.getByRole('tab', { name: 'Harness logins' }).click();
    await shared.getByRole('button', { name: 'Manage access', exact: true }).waitFor();
    assert.equal((await api(global.context, 'POST', 'accounts/' + account.id + '/default')).status(), 404);
    assert.equal((await api(global.context, 'GET', 'agents')).status(), 200);
    assert.equal((await (await api(global.context, 'GET', 'agents')).json()).length, 0);
    await shared.getByLabel('Actions for ' + account.name, { exact: true }).click();
    await user.page.getByRole('menuitem', { name: 'Leave access…' }).click();
    await user.page.getByText('0 affected agents; 0 currently running.', { exact: true }).waitFor();
    await user.page.getByRole('button', { name: 'Leave', exact: true }).click();
    await shared.waitFor({ state: 'detached' });
    assert.equal((await api(user.context, 'GET', 'accounts/' + account.id + '/impact?userId=' + recipient.id)).status(), 404);
    assert.equal(await global.page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    for (const theme of ['light', 'dark']) {
        await global.page.evaluate(value => localStorage.setItem('dam-theme', value), theme);
        await global.page.reload({ waitUntil: 'domcontentloaded' });
        await global.page.locator('.account-row').first().waitFor();
        await global.page.waitForLoadState('networkidle');
        assert.equal(await global.page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        if (process.env.DAM_SCREENSHOTS) await global.page.screenshot({ path: process.env.DAM_SCREENSHOTS + '/login-sharing-' + engine + '-' + theme + '.png', fullPage: true });
    }
    assert.deepEqual(errors, []);
    console.log('Passed (' + engine + '): sharing UI, personal defaults, Use/Manage/admin boundaries, leaving access, and phone layout.');
} catch (error) {
    for (const context of browser.contexts()) for (const page of context.pages()) {
        console.log('Failure page:', page.url(), (await page.locator('body').innerText()).slice(0, 600));
        if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: process.env.DAM_SCREENSHOTS + '/failure-' + engine + '-' + browser.contexts().indexOf(context) + '.png' }).catch(() => {});
    }
    throw error;
} finally { await browser.close(); }
