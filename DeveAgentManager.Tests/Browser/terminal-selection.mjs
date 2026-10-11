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
    const button = page.locator('.terminal-copy');
    const drag = async (row, shift) => {
        const box = await page.locator('.xterm-rows > div').nth(row).boundingBox();
        if (shift) await page.keyboard.down('Shift');
        await page.mouse.move(box.x + 2, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 4 });
        await page.mouse.move(box.x + box.width - 4, box.y + box.height / 2, { steps: 4 });
        await page.mouse.up();
        if (shift) await page.keyboard.up('Shift');
        return box;
    };
    const clipboard = () => page.evaluate(() => navigator.clipboard.readText());

    await page.goto(base + `/agents/${writable}`);
    await page.locator('.xterm-rows').getByText('alpha bravo charlie').waitFor({ timeout: 60000 });
    await page.locator('.xterm-helper-textarea').focus();

    // A plain drag goes to tmux (which copies on release itself): no selection here.
    received = '';
    await drag(0, false);
    await page.waitForTimeout(300);
    assert.match(received, /\x1b\[<32;/, 'the drag went to tmux');
    assert.equal(await button.isHidden(), true);

    // Shift+drag selects in the browser: the Copy button appears next to where the mouse was let go, inside the terminal.
    received = '';
    const row = await drag(0, true);
    await button.waitFor({ state: 'visible', timeout: 5000 });
    assert.equal(received, '', 'a Shift+drag is not sent to tmux');
    const at = await button.boundingBox();
    const frame = await page.locator('.terminal-frame').boundingBox();
    assert.ok(at.y > row.y && at.y < row.y + row.height + 40, `button below the selection's end: ${JSON.stringify({ at, row })}`);
    assert.ok(at.x >= frame.x && at.x + at.width <= frame.x + frame.width && at.y + at.height <= frame.y + frame.height, 'inside the terminal');
    await button.click();
    await page.locator('.mud-snackbar').getByText('Copied 19 characters').waitFor({ timeout: 5000 });
    assert.equal(await clipboard(), 'alpha bravo charlie');
    assert.equal(await button.isHidden(), true, 'hidden once copied');
    assert.equal(await page.evaluate(() => document.activeElement?.classList.contains('xterm-helper-textarea')), true, 'the terminal keeps the focus');

    // Ctrl+C with a selection copies instead of interrupting.
    await drag(1, true);
    await button.waitFor({ state: 'visible', timeout: 5000 });
    received = '';
    await page.keyboard.press('Control+c');
    await page.locator('.mud-snackbar').getByText('Copied 16 characters').waitFor({ timeout: 5000 });
    assert.equal(await clipboard(), 'second line here');
    assert.ok(!received.includes('\x03'), 'Ctrl+C was not sent');
    await button.waitFor({ state: 'hidden', timeout: 5000 });

    // Watching read-only: tmux's mouse mode is left out, so a plain drag selects.
    await page.goto(base + `/agents/${watched}`);
    await page.locator('.xterm-rows').getByText('alpha bravo charlie').waitFor({ timeout: 60000 });
    await drag(1, false);
    await button.waitFor({ state: 'visible', timeout: 5000 });
    await button.click();
    await page.locator('.mud-snackbar').getByText('Copied 16 characters').waitFor({ timeout: 5000 });
    assert.equal(await clipboard(), 'second line here');
    console.log(`Passed (${engine}): Shift+drag (or a plain drag when read-only) shows a Copy button where the selection ended; it and Ctrl+C copy and keep the focus; a plain drag still goes to tmux.`);
} finally {
    await browser.close();
}
