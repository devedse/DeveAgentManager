// The OS wake lock is simulated; this checks the real toolbar and browser lifecycle integration.
const { chromium } = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

const browser = await chromium.launch();
try {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    await context.addInitScript(() => {
        const mode = new URL(location.href).searchParams.get('wake-test');
        if (mode === 'insecure') Object.defineProperty(window, 'isSecureContext', { value: false });
        if (mode === 'unsupported') {
            Object.defineProperty(navigator, 'wakeLock', { value: undefined });
            return;
        }
        const state = window.wakeTest = { requests: 0, locks: [], rejectNext: false, holdNext: false } as { requests: number; locks: any[]; rejectNext: boolean; holdNext: boolean; } & Record<string, any>;
        Object.defineProperty(navigator, 'wakeLock', { value: {
            async request(type) {
                if (type !== 'screen') throw new Error('Unexpected wake lock type');
                state.requests++;
                if (state.rejectNext) { state.rejectNext = false; throw new DOMException('Denied', 'NotAllowedError'); }
                if (state.holdNext) {
                    state.holdNext = false;
                    await new Promise(resolve => { state.resolveRequest = resolve; });
                }
                const lock = new EventTarget() as EventTarget & {released: boolean; release: () => Promise<void>};
                lock.released = false;
                lock.release = async () => {
                    if (lock.released) return;
                    lock.released = true;
                    lock.dispatchEvent(new Event('release'));
                };
                state.locks.push(lock);
                return lock;
            }
        } });
        state.visibility = value => {
            Object.defineProperty(document, 'visibilityState', { configurable: true, value });
            document.dispatchEvent(new Event('visibilitychange'));
        };
    });
    const page = await context.newPage();
    const errors = [];
    const startupMessages = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
        if (message.type() === 'error') startupMessages.push(message.text().slice(0, 1200));
        if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text());
    });
    const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const preferencePath = '/api/me/settings/screen-awake';
    const savePreference = async enabled => {
        const response = await context.request.put(base + preferencePath, { data: { enabled }, headers: { 'X-DAM-Request': '1', Origin: base } });
        assert.ok(response.ok());
    };
    const savedPreference = async () => (await (await context.request.get(base + preferencePath)).json()).enabled;
    await savePreference(false);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date().toISOString();
    const agent = { id, runnerId: id, runnerName: 'Test runner', runnerState: 'Ready', runnerHarnessIds: ['codex'],
        runnerToolIds: ['vscode'], name: 'Screen awake', harnessId: 'codex', workingDirectory: '/work/example',
        state: 'Running', access: 'Owner', accountName: 'Test account', createdAt: now, updatedAt: now };
    const otherId = '00000000-0000-0000-0000-000000000002';
    const otherAgent = { ...agent, id: otherId, name: 'Another agent' };
    let failNextSave = false;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === preferencePath && route.request().method() === 'PUT' && failNextSave) {
            failNextSave = false;
            return route.fulfill({ status: 500, json: { message: 'Test-only save failure' } });
        }
        if (path === '/api/me' || path === preferencePath || path === '/api/diagnostics/cpu') return route.continue();
        const body = path === '/api/info' ? info : path === `/api/agents/${id}` ? agent
            : path === `/api/agents/${otherId}` ? otherAgent : path === '/api/agents' ? [agent, otherAgent] : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 }
            : path.endsWith('/limit') || path.endsWith('/session') ? null : [];
        await route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    const open = async (mode = undefined) => {
        await page.goto(base + `/agents/${id}` + (mode ? `?wake-test=${mode}` : ''));
        try { await page.getByRole('button', { name: /Keep screen awake/ }).waitFor({ timeout: 60000 }); }
        catch (error) {
            await page.screenshot({ path: '/tmp/dam-screen-awake-start-failure.png' });
            console.log('Startup diagnostics:', await page.locator('button').evaluateAll(buttons => buttons.map(button => button.getAttribute('aria-label'))), startupMessages, errors.map(error => error.slice(0, 600)));
            throw error;
        }
    };
    const button = () => page.getByRole('button', { name: /Keep screen awake/ });
    const waitState = async (label, pressed) => {
        await page.getByRole('button', { name: `Keep screen awake: ${label}`, exact: true }).waitFor();
        await page.waitForFunction(pressed => document.querySelector('button[aria-label^="Keep screen awake"]')?.getAttribute('aria-pressed') === String(pressed), pressed);
    };
    const assertStableViewport = async () => {
        const expectedWidth = page.viewportSize().width;
        for (let sample = 0; sample < 20; sample++) {
            const size = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth,
                scale: visualViewport.scale, layout: document.querySelector('.terminal-layout').getBoundingClientRect().width }));
            if (size.width !== expectedWidth || size.scroll > expectedWidth || size.scale !== 1 || size.layout > expectedWidth) {
                await page.screenshot({ path: '/tmp/dam-screen-awake-mobile-overflow.png' });
                assert.fail(`screen-awake tooltip resized the mobile viewport: ${JSON.stringify(size)}; expected ${expectedWidth}`);
            }
            await page.waitForTimeout(100);
        }
    };
    await open();
    await waitState('off', false);
    const offIcon = await button().locator('svg').innerHTML();
    await button().tap();
    await waitState('on', true);
    await assertStableViewport();
    assert.notEqual(await button().locator('svg').innerHTML(), offIcon, 'on/off icons must differ');
    assert.ok((await button().getAttribute('class')).includes('mud-primary-text'), 'active icon must be highlighted');
    for (const width of [1280, 740, 390, 320]) {
        await page.setViewportSize({ width, height: 844 });
        await assertStableViewport();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `toolbar overflow at ${width}`);
        if (await page.locator('[data-toolbar="awake"]').isHidden()) {
            // No room in the toolbar: the same toggle, with its state, is in the ⋮ menu.
            await page.locator('.terminal-toolbar button[aria-label="More actions"]').click();
            await page.getByRole('menuitem', { name: 'Keep screen awake: on', exact: true }).waitFor();
            await page.locator('.terminal-toolbar button[aria-label="More actions"]').click();
            await page.locator('.mud-popover-open .mud-menu-list').waitFor({ state: 'detached' });
            continue;
        }
        const box = await button().boundingBox();
        assert.ok(box.x >= 0 && box.x + box.width <= width, `toggle outside viewport at ${width}`);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: '/tmp/dam-screen-awake-on.png' });
    await page.evaluate(() => wakeTest.visibility('hidden'));
    await waitState('paused', true);
    assert.ok(await page.evaluate(() => wakeTest.locks.every(lock => lock.released)));
    await page.evaluate(() => wakeTest.visibility('visible'));
    await waitState('on', true);
    assert.equal(await page.evaluate(() => wakeTest.requests), 2, 'returning to the tab must reacquire');
    await button().tap();
    await waitState('off', false);
    await assertStableViewport();
    assert.ok(await page.evaluate(() => wakeTest.locks.every(lock => lock.released)));
    await page.evaluate(() => { wakeTest.visibility('hidden'); wakeTest.visibility('visible'); });
    assert.equal(await page.evaluate(() => wakeTest.requests), 2, 'disabled toggle must stay off');
    await button().click();
    await waitState('on', true);
    await page.evaluate(() => wakeTest.locks.at(-1).release());
    await waitState('paused', true);
    assert.equal(await savedPreference(), true, 'system revocation must preserve the preference');
    await button().click();
    await waitState('off', false);
    await page.evaluate(() => { wakeTest.rejectNext = true; });
    await button().click();
    await page.getByText('Could not keep the screen awake. Check your battery saver and browser settings.', { exact: true }).waitFor();
    await waitState('paused', true);
    assert.equal(await savedPreference(), true, 'platform rejection must preserve the preference');
    await button().click();
    await waitState('off', false);
    // The same lock stays active while moving between layouts and different agents.
    await button().click();
    await waitState('on', true);
    const requests = await page.evaluate(() => wakeTest.requests);
    await page.getByRole('link', { name: 'Back to agents', exact: true }).click();
    await page.locator('.terminal-layout').waitFor({ state: 'detached' });
    await waitState('on', true);
    assert.ok(await page.evaluate(() => !wakeTest.locks.at(-1).released), 'app navigation must keep the global lock');
    await page.locator(`a.agent-row-main[href="agents/${otherId}"]`).click();
    await page.locator('.terminal-layout').waitFor();
    await waitState('on', true);
    assert.equal(await page.evaluate(() => wakeTest.requests), requests);
    await page.reload();
    await waitState('on', true);
    assert.equal(await page.evaluate(() => wakeTest.requests), 1, 'refresh must restore the account preference');
    failNextSave = true;
    await button().click();
    await page.getByText('Could not save the screen-awake setting. Try again.', { exact: true }).waitFor();
    await waitState('on', true);
    assert.equal(await savedPreference(), true);
    // Changes made in another tab arrive through the live connection.
    const secondPage = await context.newPage();
    await secondPage.goto(base + `/agents/${id}`);
    await secondPage.getByRole('button', { name: 'Keep screen awake: on', exact: true }).waitFor({ timeout: 60000 });
    await secondPage.getByRole('button', { name: 'Keep screen awake: on', exact: true }).tap();
    await waitState('off', false);
    await page.waitForFunction(() => wakeTest.locks.every(lock => lock.released));
    await secondPage.close();
    await page.reload();
    await waitState('off', false);
    assert.equal(await page.evaluate(() => wakeTest.requests), 0, 'refresh must also preserve off');
    // A pending request may span navigation, then must release if the global preference is disabled.
    await page.evaluate(() => { wakeTest.holdNext = true; });
    await button().click();
    await page.waitForFunction(() => !!wakeTest.resolveRequest);
    await page.getByRole('link', { name: 'Back to agents', exact: true }).click();
    await page.locator('.terminal-layout').waitFor({ state: 'detached' });
    await waitState('paused', true);
    await button().click();
    await waitState('off', false);
    await page.evaluate(() => wakeTest.resolveRequest());
    await page.waitForFunction(() => wakeTest.locks.length === 1 && wakeTest.locks[0].released);
    await savePreference(true);
    for (const [mode, hint] of [['insecure', 'Keep screen awake requires HTTPS'], ['unsupported', 'Keep screen awake is unavailable in this browser']]) {
        await open(mode);
        await page.getByRole('button', { name: hint, exact: true }).waitFor();
        assert.ok(await button().isDisabled());
        assert.equal(await button().getAttribute('aria-pressed'), 'true', 'unsupported browsers must not reset the preference');
    }
    await open();
    await waitState('on', true);
    await button().click();
    await waitState('off', false);
    assert.deepEqual(errors, []);
    console.log('Passed: account preference survives navigation, agent switches and refresh; live tab updates, save failure, mobile touch and rotation, background resume, platform rejection, pending requests and unsupported browsers.');
} finally {
    await browser.close();
}
