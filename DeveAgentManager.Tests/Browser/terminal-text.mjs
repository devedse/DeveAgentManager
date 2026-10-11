const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Text selected in xterm.js gets a Copy button where the selection ended: Shift+drag where tmux has the mouse (a plain
// drag still goes to tmux), a plain drag on a read-only terminal. The button and Ctrl+C copy, say so, and keep the
// terminal focused.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch(engine === 'firefox' ? { firefoxUserPrefs: { 'dom.events.testing.asyncClipboard': true } } : {});
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...(engine === 'chromium' ? { permissions: ['clipboard-read', 'clipboard-write'] } : {}) });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const writable = '00000000-0000-0000-0000-000000000001', watched = '00000000-0000-0000-0000-000000000002', now = new Date().toISOString();
    const agent = (id, access) => ({ id, runnerId: writable, ownerId: writable, ownerName: 'Test user', access, runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'],
        runnerToolIds: [], name: `select-${access}`, harnessId: 'claude', workingDirectory: '/work/example', state: 'Running', loginOwnerId: writable, loginOwnerName: 'Test user', createdAt: now, updatedAt: now });
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        const body = path === '/api/info' ? info : path === `/api/agents/${writable}` ? agent(writable, 'Owner') : path === `/api/agents/${watched}` ? agent(watched, 'Read')
            : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') || path.endsWith('/session') ? null : [];
        return route.fulfill({ json: body });
    });

    let received = '';
    await context.routeWebSocket('**/ws/terminal?**', ws => {
        ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' }));
        // tmux turns on mouse reporting ("mouse on"), also for a read-only screen.
        ws.send(Buffer.from('\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[2J\x1b[Halpha bravo charlie\r\nsecond line here\r\n'));
        ws.onMessage(message => { if (typeof message !== 'string') received += Buffer.from(message).toString('latin1'); });
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await context.route('**/api/agents/*/terminal-text?*', route => route.fulfill({ json: { history: 'old output', screen: 'current **screen**', more: false } }));
    for (const id of [writable, watched]) {
        await page.goto(base + `/agents/${id}`);
        await page.locator('.xterm').waitFor({ timeout: 60000 });
        await page.getByRole('button', { name: 'Select terminal text', exact: true }).click();
        await page.locator('.terminal-text').waitFor();
        assert.equal(await page.locator('.terminal-text').textContent(), 'old output\ncurrent **screen**');
        await page.getByRole('button', { name: 'Copy screen', exact: true }).click();
        assert.equal(await page.evaluate(() => navigator.clipboard.readText()), 'current **screen**');
        await page.getByRole('button', { name: 'Copy all', exact: true }).click();
        assert.equal(await page.evaluate(() => navigator.clipboard.readText()), 'old output\ncurrent **screen**');
        await page.getByRole('button', { name: 'Close', exact: true }).last().click();
    }
    console.log(`Passed (${engine}): phone text viewer copies history and screen for owners and read-only viewers.`);
} finally { await browser.close(); }
