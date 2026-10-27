const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

// The live connection (LiveUpdates) and its "Reconnecting…" badge: a quick reconnect, such as a frozen background tab
// shown again, doesn't flash the badge; a longer outage shows it after 2 seconds; and a tab shown again tries at once
// instead of waiting up to 5 seconds for its next attempt. The live socket and its negotiation are controlled here.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async (condition, timeout, message) => {
    for (const end = Date.now() + timeout; !condition(); await sleep(25)) {
        if (Date.now() > end) throw new Error(message);
    }
};
const setVisibility = (page, state) => page.evaluate(state => {
    Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
    Object.defineProperty(document, 'hidden', { value: state === 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
}, state);

try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    // Records when the badge is added, becomes visible and is removed, in page time.
    await context.addInitScript(() => {
        const badge = { shown: 0, visibleFor: [], hiddenFor: [] } as Record<string, any>;
        (window as any).__badge = badge;
        let current = null, addedAt = 0, visibleAt = null;
        const check = () => {
            const element = document.querySelector('.live-offline');
            const now = performance.now();
            if (element && element !== current) { current = element; addedAt = now; visibleAt = null; badge.shown++; }
            if (current && visibleAt === null && element) {
                const style = getComputedStyle(element);
                if (style.visibility === 'visible' && Number(style.opacity) > 0) visibleAt = now;
            }
            if (!element && current) {
                current = null;
                (visibleAt === null ? badge.hiddenFor : badge.visibleFor).push(Math.round((visibleAt ?? now) - addedAt));
            }
            badge.visibleAfter = current && visibleAt !== null ? Math.round(visibleAt - addedAt) : null;
        };
        new MutationObserver(check).observe(document, { childList: true, subtree: true });
        setInterval(check, 20);
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));

    let down = false, slow = 0;
    const negotiations = [], sockets = [], builds = [];
    let socket = null;
    await context.route('**/hubs/live/negotiate**', async route => {
        negotiations.push({ at: Date.now(), ok: !down });
        if (down) return route.fulfill({ status: 503, body: 'Test-only outage' });
        if (slow) await sleep(slow);
        return route.continue();
    });
    await context.routeWebSocket('**/hubs/live**', ws => { ws.connectToServer(); socket = ws; sockets.push(Date.now()); });
    page.on('request', request => { if (new URL(request.url()).pathname === '/api/build') builds.push(Date.now()); });

    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    await until(() => sockets.length === 1, 30000, 'The live connection did not open');
    // A socket is counted when the page creates it; the connection is up once the rendered layout drops the badge.
    await page.locator('.app-bar').waitFor();
    await page.locator('.live-offline').waitFor({ state: 'detached', timeout: 30000 });
    const badge = () => page.evaluate(() => ({ ...(window as any).__badge }));
    const lose = () => { const lost = Date.now(); socket.close(); return lost; };
    const failuresSince = since => negotiations.filter(n => n.at >= since && !n.ok);

    // 1. A quick reconnect (here 600 ms, as over a real network) adds the badge but never shows it.
    slow = 600;
    let before = await badge();
    let lost = lose();
    await until(() => sockets.length === 2, 5000, 'A lost connection did not reconnect');
    await page.locator('.live-offline').waitFor({ state: 'detached', timeout: 5000 });
    let after = await badge();
    assert.equal(after.shown, before.shown + 1, 'The lost connection was noticed');
    assert.equal(await page.locator('.live-offline').count(), 0, 'The badge is removed after reconnecting');
    assert.equal(after.visibleFor.length, before.visibleFor.length, `A quick reconnect never shows the badge: ${JSON.stringify(after)}`);
    assert.ok(after.hiddenFor.at(-1) >= 500, `The badge was present but hidden while reconnecting: ${after.hiddenFor.at(-1)} ms`);
    assert.ok(builds.some(at => at > lost), 'Pages reload what they missed after reconnecting');
    slow = 0;

    // 2. A longer outage shows the badge after 2 seconds, and attempts continue every 5 seconds on their own.
    down = true;
    lost = lose();
    await until(() => failuresSince(lost).length >= 4, 15000, 'Quick attempts did not run');
    const fourth = failuresSince(lost)[3].at;
    after = await badge();
    assert.ok(after.visibleAfter >= 1900 && after.visibleAfter <= 3000, `The badge shows after 2 seconds: ${after.visibleAfter} ms`);
    assert.ok(await page.locator('.live-offline').isVisible(), 'The badge stays during the outage');
    down = false;
    await until(() => sockets.length === 3, 9000, 'Attempts did not continue after the quick ones');
    const waited = sockets[2] - fourth;
    assert.ok(waited >= 4500, `Without the tab being shown again, the next attempt waits 5 seconds: ${waited} ms`);
    await page.locator('.live-offline').waitFor({ state: 'detached' });

    // 3. A tab shown again during that wait tries at once.
    down = true;
    lost = lose();
    await until(() => failuresSince(lost).length >= 4, 15000, 'Quick attempts did not run');
    const failed = failuresSince(lost)[3].at;
    down = false;
    await sleep(400);
    await setVisibility(page, 'hidden');
    await sleep(400);
    const shown = Date.now();
    await setVisibility(page, 'visible');
    await until(() => sockets.length === 4, 4000, 'Showing the tab did not reconnect');
    assert.ok(sockets[3] - shown < 1500, `Showing the tab reconnects at once: ${sockets[3] - shown} ms after showing it`);
    assert.ok(sockets[3] - failed < 3500, `Without waiting for the next attempt: ${sockets[3] - failed} ms after the last failure`);
    await page.locator('.live-offline').waitFor({ state: 'detached' });

    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): quick reconnects keep the badge hidden, longer outages show it after 2 seconds, attempts continue every 5 seconds, and showing the tab reconnects at once.`);
} finally {
    await browser.close();
}
