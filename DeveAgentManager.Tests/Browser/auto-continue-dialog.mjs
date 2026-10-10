const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// The continue dialog in automatic mode shows the usage limit on screen and its detected reset (or that it has none),
// and switching automatic on shows the next continue straight away, without closing and reopening the dialog.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, timezoneId: 'Europe/Amsterdam' });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date();
    const agent = { id, runnerId: id, ownerId: id, ownerName: 'Test user', access: 'Owner', runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'],
        runnerToolIds: [], name: 'limited', harnessId: 'claude', workingDirectory: '/work/example', state: 'Running', loginOwnerId: id, loginOwnerName: 'Test user',
        createdAt: now.toISOString(), updatedAt: now.toISOString() };
    const reset = new Date(Date.UTC(2030, 0, 2, 13, 0)); // 14:00 in Amsterdam
    const detected = { message: "You've hit your limit · resets 2pm (Europe/Amsterdam)", timeZoneId: 'Europe/Amsterdam', detectedAt: now.toISOString(),
        resetAt: reset.toISOString(), continueAt: null, status: 'Detected', detail: null, nativeAutoContinue: false, manual: false, detectedResetAt: reset.toISOString() };
    let limit = detected;
    let automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname, method = route.request().method();
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        if (path === `/api/agents/${id}/limit/automatic` && method === 'PUT') {
            const request = route.request().postDataJSON();
            // As the Manager does: switching on schedules the limit on screen at once.
            automatic = { ...automatic, enabled: request.enabled, remaining: request.remaining ?? null,
                detail: request.enabled ? (limit.detectedResetAt ? 'Waiting until one minute after the detected reset.' : 'The reset time is uncertain. Switch to a one-time schedule to choose it manually.') : 'Automatic continuation is off.' };
            if (request.enabled && limit.detectedResetAt) limit = { ...limit, status: 'Scheduled', continueAt: new Date(reset.getTime() + 60000).toISOString() };
            return route.fulfill({ status: 204 });
        }
        if (path === `/api/agents/${id}/limit/automatic`) return route.fulfill({ json: automatic });
        if (path === `/api/agents/${id}/limit`) return route.fulfill({ json: limit });
        const body = path === '/api/info' ? info : path === `/api/agents/${id}` ? agent : path.endsWith('/session') ? null : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' })));

    const openDialog = async () => {
        await page.goto(base + `/agents/${id}`);
        await page.locator('[data-toolbar="limit"] button').waitFor({ timeout: 60000 });
        // Leaving the previous page aborts its requests; only this page's errors count.
        errors.length = 0;
        await page.locator('[data-toolbar="limit"] button').click();
        await page.getByRole('dialog').waitFor();
        await page.getByRole('dialog').locator('.mud-select').first().click();
        await page.getByRole('option', { name: /^Automatic after usage limits/ }).click();
    };

    // A limit with a reset time: shown before switching on, and the next continue right after.
    await openDialog();
    const dialog = page.getByRole('dialog');
    await dialog.getByText("You've hit your limit · resets 2pm (Europe/Amsterdam)").waitFor();
    await dialog.getByText('Detected reset: 2030-01-02 14:00').waitFor();
    await dialog.getByText('Auto-continue enabled').click();
    await dialog.getByText('Next continue:').waitFor({ timeout: 5000 });
    assert.match(await dialog.getByText('Next continue:').textContent(), /2030-01-02 14:01/);
    await dialog.getByText('Waiting until one minute after the detected reset.').waitFor();
    await page.screenshot({ path: '/tmp/dam-auto-continue-scheduled.png' });

    // A limit without a reset time: says so, instead of waiting for a limit that is already there.
    limit = { ...detected, message: "You've hit your usage limit", resetAt: null, detectedResetAt: null };
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    await openDialog();
    await dialog.getByText('No reset time was found in the message').waitFor();
    await dialog.getByText('Auto-continue enabled').click();
    await dialog.getByText('The reset time is uncertain').waitFor({ timeout: 5000 });
    assert.equal(await dialog.getByText('Waiting for a detected usage limit').count(), 0);
    assert.equal(await dialog.getByText('Next continue:').count(), 0);
    await page.screenshot({ path: '/tmp/dam-auto-continue-unknown.png' });

    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): automatic mode shows the limit on screen and its detected reset (or that it has none), and the next continue right after switching on.`);
} finally {
    await browser.close();
}
