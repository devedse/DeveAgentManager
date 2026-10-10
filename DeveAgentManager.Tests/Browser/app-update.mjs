// Use the published app: this checks the client assembly's build identity and real JS lifecycle callbacks.
const { chromium, firefox } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await (engine === 'firefox' ? firefox : chromium).launch();
try {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true,
        ...(engine === 'chromium' ? { isMobile: true } : {}) });
    await context.addInitScript(() => {
        // Capture only the update poll, keeping normal app timers intact.
        const interval = window.setInterval;
        window.updatePolls = [];
        window.setInterval = (callback, delay, ...args) => {
            if (delay === 60000) updatePolls.push(callback);
            return interval(callback, delay, ...args);
        };
        window.updateVisibility = value => {
            Object.defineProperty(document, 'visibilityState', { configurable: true, value });
            document.dispatchEvent(new Event('visibilitychange'));
        };
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
    const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5197';
    await page.goto(base + '/login');
    await page.locator('[name="username"]').fill('admin');
    await page.locator('[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('[type="submit"]').click()]);
    await page.waitForFunction(() => updatePolls.length === 1, null, { timeout: 60000 });
    const response = await context.request.get(base + '/api/build');
    assert.ok(response.ok());
    assert.equal(response.headers()['cache-control'], 'no-store');
    const current = await response.json();
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date().toISOString();
    const agent = { id, runnerId: id, runnerName: 'Test runner', runnerState: 'Ready', runnerHarnessIds: ['codex'], runnerToolIds: [],
        name: 'Update preview', harnessId: 'codex', workingDirectory: '/work/example', state: 'Running', access: 'Owner', createdAt: now, updatedAt: now };
    let deployed = current, checks = 0, fail = false;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/build') {
            checks++;
            return route.fulfill({ status: fail ? 503 : 200, json: fail ? { detail: 'Test-only restart' } : deployed, headers: { 'Cache-Control': 'no-store' } });
        }
        if (path === '/api/me' || path === '/api/me/settings/screen-awake') return route.continue();
        return route.fulfill({ json: path === '/api/info' ? info : path === `/api/agents/${id}` ? agent
            : path === '/api/agents' ? [agent] : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 }
            : path.endsWith('/limit') || path.endsWith('/session') ? null : [] });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    const card = page.getByRole('status', { name: 'Application update' });
    const settle = async () => {
        await page.waitForFunction(() => updatePolls.length === 1);
        await page.waitForTimeout(500);
    };
    const checkVisible = async () => {
        const before = checks;
        await page.evaluate(() => updateVisibility('visible'));
        for (let attempt = 0; checks <= before && attempt < 100; attempt++) await page.waitForTimeout(50);
        assert.ok(checks > before, 'returning to the tab must check the deployed build');
        await page.waitForTimeout(200);
    };
    await page.goto(base + `/agents/${id}`);
    await page.getByRole('button', { name: /Keep screen awake/ }).waitFor({ timeout: 60000 });
    await settle();
    assert.equal(await card.count(), 0, 'published client and server must agree on the build');
    await page.locator('.dam-touch-input').focus();
    const focused = await page.evaluate(() => document.activeElement.className);
    deployed = { buildId: 'test-build-2', version: '1.0.999' };
    await checkVisible();
    await card.waitFor();
    assert.equal(await page.evaluate(() => document.activeElement.className), focused, 'the update prompt must not steal focus');
    assert.ok((await card.innerText()).includes('1.0.999'));

    for (const theme of ['dark', 'light']) for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 844 });
        await page.evaluate(theme => { document.documentElement.dataset.theme = theme; }, theme);
        const box = await card.boundingBox();
        if (!(box.x >= 0 && box.x + box.width <= width && box.y + box.height <= 844)) {
            await page.screenshot({ path: '/tmp/dam-update-overflow.png' });
            console.log('Update layout:', box, await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth, scale: visualViewport.scale,
                popovers: [...document.querySelectorAll('.mud-popover')].map(p => ({ cls: p.className, box: p.getBoundingClientRect().toJSON() })) })));
        }
        assert.ok(box.x >= 0 && box.x + box.width <= width && box.y + box.height <= 844, `update card must fit at ${width}`);
        await card.getByRole('button', { name: 'Help about application updates' }).tap();
        await page.getByText('Reload to load the latest version and return to this page. Unsaved edits may be lost.', { exact: true }).waitFor();
        for (let sample = 0; sample < 5; sample++) {
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'help must not expand the mobile viewport');
            await page.waitForTimeout(100);
        }
        await card.getByRole('button', { name: 'Help about application updates' }).tap();
        await page.screenshot({ path: `/tmp/dam-app-update-${engine}-${theme}-${width}.png` });
    }
    const location = page.url();
    await card.getByRole('button', { name: 'Later', exact: true }).click();
    assert.equal(page.url(), location);
    await checkVisible();
    assert.equal(await card.count(), 0, 'Later must dismiss the same build');
    await page.getByRole('link', { name: 'Back to agents', exact: true }).click();
    await page.waitForURL(base + '/agents');
    await settle();
    await checkVisible();
    assert.equal(await card.count(), 0, 'dismissal must survive switching layouts');
    assert.equal(await page.evaluate(() => updatePolls.length), 1, 'layout changes must not install duplicate update timers');
    deployed = { buildId: 'test-build-3', version: '1.0.1000' };
    const beforeHidden = checks;
    await page.evaluate(() => { updateVisibility('hidden'); updatePolls[0](); });
    await page.waitForTimeout(200);
    assert.equal(checks, beforeHidden, 'hidden tabs must not poll');
    fail = true;
    await checkVisible();
    assert.equal(await card.count(), 0, 'temporary deployment failures must not show an update');
    fail = false;
    await checkVisible();
    await card.getByText('1.0.1000', { exact: true }).waitFor();
    // Dismiss this build, then detect another through the scheduled poll.
    await card.getByRole('button', { name: 'Later', exact: true }).click();
    deployed = { buildId: 'test-build-4', version: '1.0.1001' };
    await page.evaluate(() => updatePolls[0]());
    await card.waitFor();
    await card.getByText('1.0.1001', { exact: true }).waitFor();
    // A full document load is required: enhanced navigation would keep the old runtime alive.
    const previousUrl = page.url();
    deployed = current;
    await Promise.all([page.waitForEvent('domcontentloaded'), card.getByRole('button', { name: 'Reload to update', exact: true }).click()]);
    assert.equal(page.url(), previousUrl, 'reload must return to the same page');
    await settle();
    assert.equal(await card.count(), 0);
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): published build identity, visible-tab checks, polling, focus, light/dark phone layouts, help, dismissal across layouts, restart recovery and full document reload.`);
} finally {
    await browser.close();
}
