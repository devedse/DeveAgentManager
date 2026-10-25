const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

// The continue dialog in automatic mode shows the usage limit on screen and its detected reset (or that it has none),
// and switching automatic on shows the next continue straight away, without closing and reopening the dialog. A new
// usage limit offers to switch it on with one click, in a popdown from the button.
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
    let limit: Record<string, any> = detected;
    let automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    const schedules = [], automaticRequests = [];
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname, method = route.request().method();
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        if (path === `/api/agents/${id}/limit/automatic` && method === 'PUT') {
            const request = route.request().postDataJSON();
            automaticRequests.push(request);
            // As the Manager does: switching on schedules the limit on screen at once.
            automatic = { ...automatic, enabled: request.enabled, remaining: request.remaining ?? null,
                detail: request.enabled ? (limit.detectedResetAt ? 'Waiting until one minute after the detected reset.' : 'The reset time is uncertain. Switch to a one-time schedule to choose it manually.') : 'Automatic continuation is off.' };
            if (request.enabled && limit.detectedResetAt) limit = { ...limit, status: 'Scheduled', continueAt: new Date(new Date(limit.detectedResetAt).getTime() + 60000).toISOString() };
            return route.fulfill({ status: 204 });
        }
        if (path === `/api/agents/${id}/limit/automatic`) return route.fulfill({ json: automatic });
        if (path === `/api/agents/${id}/limit/continue` && method === 'POST') {
            schedules.push(route.request().postDataJSON());
            return route.fulfill({ status: 204 });
        }
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

    // Enabling the policy is not proof that it can currently continue. Check the toolbar
    // independently of the dialog, including the phone's narrow toolbar and both themes.
    const checkStatus = async (hint, color) => {
        await page.goto(base + `/agents/${id}`);
        const button = page.getByRole('button', { name: hint, exact: true });
        await button.waitFor({ timeout: 60000 });
        assert.equal(await button.evaluate((element, color) => element.classList.contains(`mud-${color}-text`), color), true, hint);
        return button;
    };
    automatic = { enabled: true, remaining: null, sent: 18, available: true, detail: 'The reset time is uncertain. Switch to a one-time schedule to choose it manually.' };
    await context.addInitScript(() => localStorage.setItem('dam-theme', 'dark'));
    await page.setViewportSize({ width: 390, height: 844 });
    await checkStatus('Auto-continue needs attention: reset time unknown', 'warning');
    await page.getByRole('button', { name: 'Auto-continue needs attention: reset time unknown', exact: true }).click();
    await dialog.locator('.mud-alert-outlined-warning, .mud-alert-text-warning, .mud-alert-filled-warning').first().waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'unknown-reset dialog overflows on phone');
    await page.screenshot({ path: '/tmp/dam-auto-continue-warning-phone.png' });

    limit = { ...detected, status: 'Scheduled', continueAt: new Date(reset.getTime() + 60000).toISOString() };
    automatic.detail = 'Waiting until one minute after the detected reset.';
    await checkStatus('Auto-continue scheduled for 2030-01-02 14:01 (Europe/Amsterdam)', 'info');
    limit = { ...limit, continueAt: '2020-01-01T00:00:00Z', detail: 'Waiting for an empty prompt. Your draft will not be submitted.' };
    automatic.detail = limit.detail;
    await checkStatus('Auto-continue waiting; view reason', 'warning');

    limit = { ...detected, status: 'Sent' };
    automatic.detail = 'Continue submitted. Waiting for the harness to resume.';
    await checkStatus('Continue submitted; waiting for the agent to resume', 'warning');
    await page.getByRole('button', { name: 'Continue submitted; waiting for the agent to resume', exact: true }).click();
    await dialog.getByText(detected.message, { exact: true }).waitFor();
    await dialog.getByText('Continue was submitted; the usage limit has not cleared.', { exact: true }).waitFor();

    limit = { ...detected, status: 'Uncertain' };
    await checkStatus('Continue delivery uncertain; check the terminal', 'error');
    limit = { ...detected, manual: true, detectedResetAt: null, detectionProblem: 'Usage-limit detection is temporarily unavailable. Saved settings are retained; retry detection.' };
    await checkStatus('Auto-continue on · detection unavailable', 'warning');
    limit = null;
    automatic.detail = 'Waiting for a detected usage limit.';
    await checkStatus('Auto-continue on · unlimited', 'success');

    // A dated weekly reset in the repeated clock hour has a precise, conservative UTC instant.
    const weekly = { ...detected, message: "You've hit your weekly limit · resets Oct 25, 2am (Europe/Amsterdam) /upgrade to increase your usage limit.",
        resetAt: '2026-10-25T01:00:00Z', detectedResetAt: '2026-10-25T01:00:00Z',
        detail: 'Clocks repeat; using the later occurrence (01:00 UTC).' };
    limit = { ...weekly };
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    await openDialog();
    await dialog.getByText(/Detected reset: 2026-10-25 02:00 \(Europe\/Amsterdam, UTC\+01:00\)/).waitFor();
    assert.equal(await dialog.getByText(/No reset time was found/).count(), 0);
    await dialog.getByText('Auto-continue enabled', { exact: true }).click();
    await dialog.getByText(/Next continue:.*2026-10-25 02:01/).waitFor();
    await page.screenshot({ path: '/tmp/dam-weekly-dst-automatic.png' });

    const oneTime = async () => {
        limit = { ...weekly };
        automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
        await openDialog();
        await dialog.locator('.mud-select').first().click();
        await page.getByRole('option', { name: 'One-time schedule', exact: true }).click();
        await dialog.getByRole('button', { name: 'Use detected reset time (+1 min)', exact: true }).click();
    };
    await oneTime();
    await dialog.getByText(/using the later occurrence/).waitFor();
    await page.screenshot({ path: '/tmp/dam-weekly-dst-one-time.png' });
    await dialog.getByRole('button', { name: 'Schedule continue', exact: true }).click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(new Date(schedules.at(-1).resetAtUtc).toISOString(), weekly.detectedResetAt.replace('Z', '.000Z'), 'detected UTC offset survives local picker round-trip');

    await oneTime();
    const count = schedules.length;
    await dialog.getByRole('textbox', { name: 'Reset time', exact: true }).fill('02:30');
    await dialog.getByRole('textbox', { name: 'Reset time', exact: true }).press('Tab');
    await dialog.getByRole('button', { name: 'Schedule continue', exact: true }).click();
    await dialog.getByText(/This local time occurs twice/).waitFor();
    assert.equal(schedules.length, count, 'editing to another ambiguous clock still requires an exact choice');
    await dialog.getByRole('textbox', { name: 'Reset time', exact: true }).fill('03:00');
    await dialog.getByRole('textbox', { name: 'Reset time', exact: true }).press('Tab');
    await dialog.getByRole('button', { name: 'Schedule continue', exact: true }).click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(new Date(schedules.at(-1).resetAtUtc).toISOString(), '2026-10-25T02:00:00.000Z', 'a changed picker uses its newly selected instant');

    limit = { ...weekly, resetAt: null, detectedResetAt: null, detail: 'This reset falls in a daylight-saving clock change. Use a duration to choose an exact continuation time.' };
    await openDialog();
    await dialog.getByText('The reset time needs confirmation.', { exact: true }).waitFor();
    assert.equal(await dialog.getByText(/No reset time was found/).count(), 0, 'an uncertain parsed reset is not described as missing');
    await page.keyboard.press('Escape');

    // A new limit that automatic mode can handle, while it is off, offers to turn it on with one click: the offer drops
    // down from the button. Each limit is a new observation (detectedAt), as the Manager records it.
    let observed = Date.UTC(2029, 11, 31, 9, 0);
    const newLimit = (changes = {}) => {
        observed += 60000;
        limit = { ...detected, detectedAt: new Date(observed).toISOString(), ...changes };
    };
    const offer = page.getByRole('status', { name: 'Turn on auto-continue' });
    const showAgent = async () => {
        await page.goto(base + `/agents/${id}`);
        await page.locator('.terminal-toolbar [aria-label="More actions"]').waitFor({ timeout: 60000 });
        errors.length = 0;
    };
    const offered = async () => {
        await showAgent();
        await offer.waitFor({ timeout: 10000 });
        await offer.getByRole('heading', { name: 'Usage limit until 2030-01-02 14:00', exact: true }).waitFor();
    };
    const notOffered = async (why) => {
        await showAgent();
        await page.locator('[data-toolbar="limit"] button').waitFor();
        // The offer would appear with the button's status; give it the same time as that.
        await page.waitForTimeout(1500);
        assert.equal(await offer.count(), 0, why);
    };
    const below = async (anchor) => {
        // Past its opening transition.
        await page.waitForTimeout(400);
        const [a, o] = [await anchor.boundingBox(), await offer.boundingBox()];
        assert.ok(o.y >= a.y + a.height - 1 && o.y - (a.y + a.height) < 24, 'the offer drops down right under its button');
        // Lined up with the button's right edge where there's room; on a phone, moved left as far as it needs to.
        const room = a.x + a.width >= o.width + 8;
        assert.ok(room ? Math.abs(o.x + o.width - (a.x + a.width)) < 4 : o.x < a.x && o.x + o.width > a.x + a.width, 'the offer is under its button');
        assert.ok(o.x >= 0 && o.x + o.width <= page.viewportSize().width, 'the offer stays on screen');
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'the offer makes the page scroll sideways');
    };
    await page.setViewportSize({ width: 1280, height: 900 });
    newLimit();
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    await offered();
    await offer.getByText('Turn on auto-continue? It continues at 2030-01-02 14:01.', { exact: true }).waitFor();
    await below(page.locator('[data-toolbar="limit"] button'));
    await page.screenshot({ path: '/tmp/dam-auto-continue-offer.png' });
    let requests = automaticRequests.length;
    await offer.getByRole('button', { name: 'Turn on', exact: true }).click();
    await offer.waitFor({ state: 'hidden', timeout: 10000 });
    assert.deepEqual(automaticRequests.slice(requests), [{ enabled: true, remaining: null }], 'one click switches on unlimited auto-continue');
    await page.getByRole('button', { name: 'Auto-continue scheduled for 2030-01-02 14:01 (Europe/Amsterdam)', exact: true }).waitFor({ timeout: 10000 });

    // Not now hides it for this limit, also after reloading; the next limit is offered again.
    newLimit();
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    await offered();
    requests = automaticRequests.length;
    await offer.getByRole('button', { name: 'Not now' }).click();
    await offer.waitFor({ state: 'hidden' });
    await notOffered('Not now hides the offer for the same limit');
    assert.equal(automaticRequests.length, requests, 'Not now changes nothing');
    newLimit();
    await offered();

    // Opening the options answers it too.
    await page.locator('[data-toolbar="limit"] button').click();
    await page.getByRole('dialog').waitFor();
    assert.equal(await offer.count(), 0, 'the offer closes when the options open');
    await page.keyboard.press('Escape');
    await notOffered('the options were seen for this limit');

    // A count of continuations left from before is kept, as the dialog's switch keeps it.
    newLimit();
    automatic = { enabled: false, remaining: 3, sent: 4, detail: 'Automatic continuation is off.', available: true };
    await offered();
    await offer.getByText('Turn on auto-continue? It continues at 2030-01-02 14:01 (3 continuations left).', { exact: true }).waitFor();
    requests = automaticRequests.length;
    await offer.getByRole('button', { name: 'Turn on', exact: true }).click();
    await offer.waitFor({ state: 'hidden', timeout: 10000 });
    assert.deepEqual(automaticRequests.slice(requests), [{ enabled: true, remaining: 3 }]);

    // Only for what one click can handle.
    newLimit();
    automatic = { enabled: true, remaining: null, sent: 0, detail: 'Usage limit detected. Preparing automatic continuation.', available: true };
    await notOffered('auto-continue is already on');
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    newLimit({ message: "You've hit your usage limit", resetAt: null, detectedResetAt: null });
    await notOffered('without a reset time, automatic mode can only ask for one');
    newLimit({ nativeAutoContinue: true });
    await notOffered('Claude continues by itself');
    newLimit({ status: 'Scheduled', continueAt: new Date(reset.getTime() + 60000).toISOString() });
    await notOffered('a one-time continue is already scheduled');
    newLimit();
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: false };
    await notOffered('pi has no recognized reset times');

    // Phones, in the dark theme: on the button while the toolbar has room for it, otherwise on the ⋮ menu that has it.
    automatic = { enabled: false, remaining: null, sent: 0, detail: null, available: true };
    // 280px is a folding phone's outer screen.
    for (const width of [390, 320, 280]) {
        await page.setViewportSize({ width, height: 800 });
        newLimit();
        await offered();
        const inMenu = await page.locator('[data-toolbar="limit"]').evaluate(element => element.hidden);
        await below(inMenu ? page.locator('.terminal-toolbar [aria-label="More actions"]') : page.locator('[data-toolbar="limit"] button'));
        await page.screenshot({ path: `/tmp/dam-auto-continue-offer-${width}.png` });
        console.log(`${width}px: the offer drops down from ${inMenu ? 'the ⋮ menu' : 'the usage-limit button'}`);
        if (width === 280) {
            await offer.getByRole('button', { name: 'Turn on', exact: true }).click();
            await offer.waitFor({ state: 'hidden', timeout: 10000 });
        }
    }
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): detected/unknown resets, dated DST overlap scheduling with UTC offset, edited picker guards, blocked delivery, unresolved sends and automatic mode, the one-click offer, including on phones.`);
} finally {
    await browser.close();
}
