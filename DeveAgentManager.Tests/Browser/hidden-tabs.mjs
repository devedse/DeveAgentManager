const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// A tab left open in the background doesn't keep the Manager busy: its sections neither poll nor reload while it is
// hidden, and catch up with one reload when it is shown again (LiveComponent, PageVisibility). Only notifications keep
// polling, so their sounds still play: each of their reads lists the agents too, and is not the page's.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const now = new Date().toISOString();

const setVisibility = (page, state) => page.evaluate(state => {
    Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
    Object.defineProperty(document, 'hidden', { value: state === 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
}, state);

try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const me = await (await context.request.get(base + '/api/me')).json();
    const agent = { id: '00000000-0000-0000-0000-000000000001', runnerId: '00000000-0000-0000-0000-000000000002', ownerId: me.id, ownerName: 'Admin',
        access: 'Owner', runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'], runnerToolIds: [], name: 'example-agent',
        harnessId: 'claude', workingDirectory: '/work/example', state: 'Running', createdAt: now, updatedAt: now };
    let lists = 0, notifications = 0;
    const pageLists = () => lists - notifications;
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/info' || path === '/api/diagnostics/cpu') return route.continue();
        if (path === '/api/agents' || path === '/api/agents/') { lists++; return route.fulfill({ json: [agent] }); }
        if (path === '/api/agents/attention') { notifications++; return route.fulfill({ json: [] }); }
        return route.fulfill({ json: [] });
    });

    // The Agents page polls every 5 seconds while it is shown.
    await page.goto(base + '/agents');
    await page.getByText('example-agent').first().waitFor({ timeout: 60000 });
    await page.waitForTimeout(500);
    const shown = pageLists();
    await page.waitForTimeout(6000);
    assert.ok(pageLists() > shown, 'A shown page keeps polling');

    await setVisibility(page, 'hidden');
    await page.waitForTimeout(500);
    const hidden = pageLists();
    await page.waitForTimeout(12000);
    assert.equal(pageLists(), hidden, 'A hidden tab makes no requests of its own');

    await setVisibility(page, 'visible');
    await page.waitForTimeout(2500);
    // The catch-up, and at most one regular poll that fell in the same moments.
    assert.ok(pageLists() >= hidden + 1 && pageLists() <= hidden + 2, `Shown again: one reload catches up (${pageLists() - hidden})`);
    const caughtUp = pageLists();
    await page.waitForTimeout(5500);
    assert.ok(pageLists() > caughtUp, 'Polling resumes');
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): a hidden tab neither polls nor reloads, and catches up with one reload when shown again.`);
} finally {
    await browser.close();
}
