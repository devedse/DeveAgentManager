const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Copying out of the terminal: what a program copies with OSC 52 (tmux on a mouse selection, Claude Code's /copy through
// tmux) goes to this device's clipboard, right after the user did something in the terminal, once, and says so. Nothing
// is copied unprompted or long after, on a read-only terminal, or when it is too large; reading the clipboard is never
// answered; when the browser refuses, the message's Copy button copies it.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch(engine === 'firefox' ? { firefoxUserPrefs: { 'dom.events.testing.asyncClipboard': true } } : {});
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...(engine === 'chromium' ? { permissions: ['clipboard-read', 'clipboard-write'] } : {}) });
    // Records what reaches the clipboard; __refuse makes the browser refuse that many copies.
    await context.addInitScript(() => {
        window.__copied = [];
        window.__refuse = 0;
        const clipboard = navigator.clipboard;
        const original = clipboard.writeText.bind(clipboard);
        clipboard.writeText = async text => {
            if (window.__refuse > 0) {
                window.__refuse--;
                throw new DOMException('Write permission denied.', 'NotAllowedError');
            }
            await original(text);
            window.__copied.push(text);
        };
    });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const writable = '00000000-0000-0000-0000-000000000001', watched = '00000000-0000-0000-0000-000000000002', now = new Date().toISOString();
    const agent = (id, access) => ({ id, runnerId: writable, ownerId: writable, ownerName: 'Test user', access, runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'],
        runnerToolIds: [], name: `copy-${access}`, harnessId: 'claude', workingDirectory: '/work/example', state: 'Running', loginOwnerId: writable, loginOwnerName: 'Test user', createdAt: now, updatedAt: now });
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        const body = path === '/api/info' ? info : path === `/api/agents/${writable}` ? agent(writable, 'Owner') : path === `/api/agents/${watched}` ? agent(watched, 'Read')
            : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') || path.endsWith('/session') ? null : [];
        return route.fulfill({ json: body });
    });

    // The Manager's side of the terminal socket: the test sends what tmux would, and keeps what the browser sends back.
    const sockets = new Map();
    let received = '';
    await context.routeWebSocket('**/ws/terminal?**', ws => {
        sockets.set(new URL(ws.url()).searchParams.get('id'), ws);
        ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' }));
        // Mouse reporting on, as tmux's "mouse on" asks.
        ws.send(Buffer.from('copy test\r\n\x1b[?1000h\x1b[?1002h\x1b[?1006h'));
        ws.onMessage(message => { if (typeof message !== 'string') received += Buffer.from(message).toString('latin1'); });
    });
    const osc52 = (text, selection = 'c') => Buffer.from(`\x1b]52;${selection};${Buffer.from(text, 'utf8').toString('base64')}\x07`);
    const copied = () => page.evaluate(() => window.__copied);
    const messages = () => page.locator('.mud-snackbar').allInnerTexts();

    await page.goto(base + `/agents/${writable}`);
    await page.locator('.xterm-rows').getByText('copy test').waitFor({ timeout: 60000 });
    const ws = sockets.get(writable);

    // Unprompted: a program copying while nobody did anything in the terminal copies nothing.
    ws.send(osc52('unprompted'));
    await page.waitForTimeout(1000);
    assert.deepEqual(await copied(), []);
    assert.deepEqual(await messages(), []);

    // Claude Code's /copy through tmux: tmux passes it on twice (load-buffer -w without a selection, then raw). Copied once.
    await page.locator('.xterm-helper-textarea').focus();
    await page.keyboard.type('/copy');
    await page.keyboard.press('Enter');
    ws.send(osc52('copied by tmux ✓', ''));
    ws.send(osc52('copied by tmux ✓'));
    await page.locator('.mud-snackbar').getByText('Copied 16 characters').waitFor({ timeout: 5000 });
    await page.waitForTimeout(500);
    assert.deepEqual(await copied(), ['copied by tmux ✓']);
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), 'copied by tmux ✓', 'the real clipboard has it');

    // Asking to read the clipboard is never answered.
    received = '';
    ws.send(Buffer.from('\x1b]52;c;?\x07'));
    await page.waitForTimeout(1000);
    assert.ok(!received.includes(']52'), `answered a clipboard read: ${JSON.stringify(received)}`);

    // A mouse selection in tmux: copied when the mouse is let go.
    const box = await page.locator('.xterm-screen').boundingBox();
    await page.mouse.move(box.x + 10, box.y + 8);
    await page.mouse.down();
    await page.mouse.move(box.x + 60, box.y + 8, { steps: 4 });
    await page.mouse.up();
    assert.match(received, /\x1b\[<32;\d+;\d+M[\s\S]*\x1b\[<0;\d+;\d+m/, 'the drag went to tmux');
    ws.send(osc52('selected with the mouse', ''));
    await page.locator('.mud-snackbar').getByText('Copied 23 characters').waitFor({ timeout: 5000 });
    assert.deepEqual((await copied()).at(-1), 'selected with the mouse');

    // Too large.
    await page.keyboard.type('x');
    ws.send(osc52('x'.repeat(1024 * 1024 + 10)));
    await page.locator('.mud-snackbar').getByText('Not copied: the terminal copies up to 1 MB.').waitFor({ timeout: 5000 });
    assert.equal((await copied()).length, 2);

    // The browser refuses: the message's Copy button copies it, in the tap.
    await page.evaluate(() => window.__refuse = 1);
    await page.keyboard.type('y');
    ws.send(osc52('the browser refused this'));
    const refused = page.locator('.mud-snackbar').filter({ hasText: "The browser didn't allow copying 24 characters from the terminal." });
    await refused.waitFor({ timeout: 5000 });
    await refused.getByRole('button', { name: 'Copy' }).click();
    await page.locator('.mud-snackbar').getByText('Copied 24 characters').waitFor({ timeout: 5000 });
    assert.deepEqual((await copied()).at(-1), 'the browser refused this');

    // Long after the last key: not copied.
    await page.keyboard.type('z');
    await page.waitForTimeout(5500);
    ws.send(osc52('too late'));
    await page.waitForTimeout(1000);
    assert.ok(!(await copied()).includes('too late'));

    // Watching read-only: what arrives was copied by someone else. Not copied, also right after a click.
    await page.goto(base + `/agents/${watched}`);
    await page.locator('.xterm-rows').getByText('copy test').waitFor({ timeout: 60000 });
    await page.evaluate(() => window.__copied = []);
    await page.locator('.xterm-screen').click();
    await page.keyboard.type('w');
    sockets.get(watched).send(osc52('copied by someone else'));
    await page.waitForTimeout(1000);
    assert.deepEqual(await copied(), []);
    assert.ok(!(await messages()).some(text => text.includes('Copied')));
    console.log(`Passed (${engine}): OSC 52 copies right after input, once, and says so; not unprompted, late, read-only or too large; reads aren't answered; a refused copy has a Copy button.`);
} finally {
    await browser.close();
}
