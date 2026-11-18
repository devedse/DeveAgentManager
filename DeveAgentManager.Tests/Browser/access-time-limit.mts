// Timed access, against a disposable Manager: a time limit given with access (a duration), changed to a date and time in
// the browser's timezone, refused in the past or when it would leave a login without a lasting manager, and removed.
import assert from 'node:assert/strict';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5197';
const password = process.env.DAM_TEST_PASSWORD ?? 'preview-test-only';
const zone = 'Europe/Amsterdam';
const browser = await playwright[engine].launch();
const suffix = engine.slice(0, 2) + Date.now().toString(36);
const errors = [];
// "YYYY-MM-DD HH:mm" in the test's timezone.
const local = (date: Date) => new Intl.DateTimeFormat('sv-SE', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).format(date);
async function api(context, method, path, data = undefined) {
    return context.request.fetch(base + '/api/' + path, { method, data, headers: { Origin: base, 'X-DAM-Request': '1' } });
}
try {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, timezoneId: zone });
    const page = await context.newPage();
    page.setDefaultTimeout(60000);
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login', { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/login')), page.locator('button[type="submit"]').click()]);
    await page.waitForLoadState('networkidle');

    const recipientName = 'timed-' + suffix;
    const created = await api(context, 'POST', 'users', { username: recipientName, password, roles: 'None' });
    assert.ok(created.ok(), await created.text());
    const recipient = await created.json();
    const added = await api(context, 'POST', 'accounts', { harnessId: 'claude', name: 'Work ' + suffix, kind: 'Subscription', secret: 'test-only-placeholder-token' });
    assert.ok(added.ok(), await added.text());
    const account = await added.json();
    const grant = async userId => (await (await api(context, 'GET', `accounts/${account.id}/grants`)).json()).find(g => g.userId === userId);

    await page.goto(base + '/harness-logins', { waitUntil: 'domcontentloaded' });
    await page.locator('.account-row').filter({ hasText: account.name }).getByRole('button', { name: 'Manage access', exact: true }).click();
    const access = page.getByRole('dialog').filter({ hasText: 'People with access' });
    await access.getByRole('button', { name: 'Add user', exact: true }).click();

    // Adding with a time limit: five hours, counted on the Manager's clock.
    const add = page.getByRole('dialog').filter({ hasText: 'Give login access' });
    await add.getByLabel('Person', { exact: true }).click();
    await page.getByRole('option').filter({ hasText: recipientName }).click();
    await add.getByRole('switch', { name: 'Time limit', exact: true }).check();
    await add.getByRole('switch', { name: 'For a duration', exact: true }).waitFor();
    assert.equal(await add.getByRole('switch', { name: 'For a duration', exact: true }).isChecked(), true);
    await add.getByText(/^Ends about \d{4}-\d\d-\d\d \d\d:\d\d \(Europe\/Amsterdam\)\.$/).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'the time limit fits a phone');
    if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: `${process.env.DAM_SCREENSHOTS}/access-time-limit-add-${engine}.png` });
    const before = Date.now();
    await add.getByRole('button', { name: 'Add', exact: true }).click();
    const row = access.locator('.access-person').filter({ hasText: recipientName });
    await row.locator('.access-expiry').filter({ hasText: /until \d{4}-\d\d-\d\d \d\d:\d\d \((4 hours, \d+ minutes|5 hours) left\)/ }).waitFor();
    const fiveHours = new Date((await grant(recipient.id)).expiresAt).getTime();
    assert.ok(fiveHours >= before + 5 * 3600_000 - 2000 && fiveHours <= Date.now() + 5 * 3600_000 + 2000, 'five hours from now');
    assert.ok((await row.locator('.access-expiry').innerText()).includes(`until ${local(new Date(fiveHours))} (`), 'shown in local time');

    // Changing it to a date and time: entered by hand, in the browser's timezone.
    const openTimeLimit = async name => {
        await access.getByLabel('Access for ' + name, { exact: true }).click();
        await page.getByRole('menuitem', { name: 'Time limit…' }).click();
        const dialog = page.getByRole('dialog').filter({ hasText: 'Time limit for' });
        await dialog.getByRole('switch', { name: 'Time limit', exact: true }).waitFor();
        return dialog;
    };
    let dialog = await openTimeLimit(recipientName);
    await dialog.getByText(`Ends ${local(new Date(fiveHours))} (${zone}) now.`, { exact: true }).waitFor();
    assert.equal(await dialog.getByRole('switch', { name: 'For a duration', exact: true }).isChecked(), false, 'a set time limit opens as its date and time');
    await dialog.getByText('Your local timezone: ' + zone, { exact: true }).waitFor();
    assert.equal(await dialog.getByRole('textbox', { name: 'End time', exact: true }).inputValue(), local(new Date(fiveHours)).slice(11));
    const day = local(new Date(Date.now() + 2 * 86400_000)).slice(0, 10);
    await dialog.getByRole('textbox', { name: 'End date', exact: true }).fill(day);
    await dialog.getByRole('textbox', { name: 'End date', exact: true }).press('Tab');
    await dialog.getByRole('textbox', { name: 'End time', exact: true }).fill('14:30');
    await dialog.getByRole('textbox', { name: 'End time', exact: true }).press('Tab');
    if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: `${process.env.DAM_SCREENSHOTS}/access-time-limit-until-${engine}.png` });
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(local(new Date((await grant(recipient.id)).expiresAt)), `${day} 14:30`, 'the local date and time is the access end');
    await row.locator('.access-expiry').filter({ hasText: `until ${day} 14:30` }).waitFor();

    // The past is refused in the dialog, which stays open.
    dialog = await openTimeLimit(recipientName);
    await dialog.getByRole('textbox', { name: 'End date', exact: true }).fill(local(new Date(Date.now() - 86400_000)).slice(0, 10));
    await dialog.getByRole('textbox', { name: 'End date', exact: true }).press('Tab');
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await dialog.getByText('Choose a time in the future.', { exact: true }).waitFor();
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await dialog.waitFor({ state: 'hidden' });

    // The Manager's rules show in the dialog too: the only manager without a time limit can't get one.
    dialog = await openTimeLimit('admin');
    await dialog.getByText('No time limit now.', { exact: true }).waitFor();
    assert.equal(await dialog.getByRole('switch', { name: 'Time limit', exact: true }).isChecked(), true);
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await dialog.getByText(/Add another enabled manager without a time limit/).waitFor();
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal((await grant((await (await api(context, 'GET', 'me')).json()).id)).expiresAt ?? null, null);

    // Switching it off removes the time limit.
    dialog = await openTimeLimit(recipientName);
    await dialog.getByRole('switch', { name: 'Time limit', exact: true }).uncheck();
    await dialog.getByRole('button', { name: 'Save', exact: true }).click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal((await grant(recipient.id)).expiresAt ?? null, null);
    await row.locator('.access-expiry').waitFor({ state: 'detached' });

    for (const theme of ['light', 'dark']) {
        await page.evaluate(value => localStorage.setItem('dam-theme', value), theme);
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForLoadState('networkidle');
        await page.locator('.account-row').filter({ hasText: account.name }).getByRole('button', { name: 'Manage access', exact: true }).click();
        dialog = await openTimeLimit(recipientName);
        await dialog.getByText(/^Ends about /).waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: `${process.env.DAM_SCREENSHOTS}/access-time-limit-${engine}-${theme}.png` });
        await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        await access.getByRole('button', { name: 'Done', exact: true }).click();
    }
    assert.deepEqual(errors, []);
    console.log('Passed (' + engine + '): time limits as a duration and a local date and time, past and manager refusals, removal, phone layout.');
} catch (error) {
    for (const context of browser.contexts()) for (const page of context.pages()) {
        console.log('Failure page:', page.url(), (await page.locator('body').innerText()).slice(0, 800));
        if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: process.env.DAM_SCREENSHOTS + '/failure-' + engine + '.png' }).catch(() => {});
    }
    throw error;
} finally { await browser.close(); }
