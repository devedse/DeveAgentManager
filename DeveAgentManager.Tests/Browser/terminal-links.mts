const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, hasTouch: true, isMobile: engine === 'chromium' });
    await context.addInitScript(() => { window.__opened = []; window.open = (...args) => { window.__opened.push(args); return null; }; });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date().toISOString();
    let access = 'Owner';
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        return route.fulfill({ json: path === '/api/info' ? info : path === `/api/agents/${id}` ? { id, runnerId: id, ownerId: id, access, ownerName: 'Test user', runnerName: 'runner', runnerState: 'Ready', runnerHarnessIds: ['codex'], runnerToolIds: [], name: 'Link test', harnessId: 'codex', workingDirectory: '/work/example', state: 'Running', createdAt: now, updatedAt: now }
            : path.endsWith('/session') || path.endsWith('/limit') ? null : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : [] });
    });
    let socket, received = '';
    await context.routeWebSocket('**/ws/terminal?**', ws => {
        socket = ws;
        ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' }));
        ws.onMessage(message => { if (typeof message !== 'string') received += Buffer.from(message).toString('latin1'); });
    });
    const lines = [
        'https://example.com/report#results',
        '\x1b]8;;https://example.org/named#details\x07Named report\x1b]8;;\x07',
        '\x1b]8;;javascript:alert(1)\x07Unsafe link\x1b]8;;\x07',
        'ordinary terminal text',
    ];
    const screen = page.locator('.xterm-screen');
    const opened = () => page.evaluate(() => window.__opened);
    async function point(row, column = 4) {
        return page.evaluate(({ row, column }) => {
            const object = [...XtermBlazor._terminals.values()][0];
            const terminal = object.terminal, box = terminal.element.querySelector('.xterm-screen').getBoundingClientRect();
            return { x: box.x + (column - .5) * box.width / terminal.cols, y: box.y + (row - .5) * box.height / terminal.rows };
        }, { row, column });
    }
    async function touch(row, swipe = false) {
        const p = await point(row);
        if (engine === 'chromium' && !swipe) return page.touchscreen.tap(p.x, p.y);
        await page.evaluate(({ p, swipe }) => {
            const screen = document.querySelector('.xterm-screen');
            const touch = new Touch({ identifier: 1, target: screen, clientX: p.x, clientY: p.y });
            screen.dispatchEvent(new TouchEvent('touchstart', { bubbles: true, cancelable: true, touches: [touch], changedTouches: [touch] }));
            if (swipe) {
                const moved = new Touch({ identifier: 1, target: screen, clientX: p.x, clientY: p.y + 35 });
                screen.dispatchEvent(new TouchEvent('touchmove', { bubbles: true, cancelable: true, touches: [moved], changedTouches: [moved] }));
            }
        }, { p, swipe });
        await page.waitForTimeout(100);
        await page.evaluate(p => {
            const screen = document.querySelector('.xterm-screen');
            screen.dispatchEvent(new TouchEvent('touchend', { bubbles: true, cancelable: true, touches: [], changedTouches: [new Touch({ identifier: 1, target: screen, clientX: p.x, clientY: p.y })] }));
        }, p);
    }
    for (const transport of ['direct', 'tmux']) for (const width of [1280, 390]) {
        await page.setViewportSize({ width, height: 850 });
        await page.goto(base + `/agents/${id}`);
        // Let lazy WASM resources finish before another full navigation can cancel their downloads.
        await page.waitForLoadState('networkidle');
        await screen.waitFor({ timeout: 60000 });
        await page.waitForTimeout(700);
        if (transport === 'tmux') {
            const size = await page.evaluate(() => {
                const terminal = [...XtermBlazor._terminals.values()][0].terminal;
                return { cols: terminal.cols, rows: terminal.rows };
            });
            const fixture = spawnSync('python3', [fileURLToPath(new URL('../../../DeveAgentManager.Tests/Browser/tmux-link-stream.py', import.meta.url)),
                '--cols', String(size.cols), '--rows', String(size.rows)], { timeout: 10000 });
            assert.equal(fixture.status, 0, fixture.stderr?.toString());
            assert.ok(fixture.stdout.includes(Buffer.from('https://example.org/named#details')), 'tmux must preserve the named link target');
            socket.send(fixture.stdout);
        } else {
            socket.send(Buffer.from('\x1b[2J\x1b[H' + lines.join('\r\n') + '\r\n\x1b[?1000h\x1b[?1002h\x1b[?1006h'));
        }
        await page.locator('.xterm-rows').getByText('Named report', { exact: false }).waitFor();
        await page.waitForTimeout(200);
        received = '';
        let before = (await opened()).length;
        const url = await point(1);
        await page.mouse.move(url.x, url.y);
        await page.waitForTimeout(150);
        await page.mouse.click(url.x, url.y);
        await page.waitForTimeout(100);
        assert.equal((await opened()).length, ++before, 'plain URL opens on this device');
        assert.equal(received, '', 'link click must not reach Runner');
        assert.equal((await opened()).at(-1)[0], 'https://example.com/report#results');
        received = '';
        await touch(2);
        await page.waitForTimeout(150);
        assert.equal((await opened()).length, ++before, 'named OSC 8 link opens with a finger');
        assert.equal((await opened()).at(-1)[0], 'https://example.org/named#details');
        assert.equal(received, '', 'touch must not ask Codex to open a Runner browser');
        await touch(1);
        await page.waitForTimeout(150);
        assert.equal((await opened()).length, ++before, 'plain URL opens with a finger');
        await touch(2, true);
        assert.equal((await opened()).length, before, 'swipe must not open a URL');
        const unsafe = await point(3);
        await page.mouse.move(unsafe.x, unsafe.y);
        await page.waitForTimeout(150);
        await page.mouse.click(unsafe.x, unsafe.y);
        assert.equal((await opened()).length, before, 'unsafe protocol never opens');
        received = '';
        const ordinary = await point(4);
        await page.mouse.click(ordinary.x, ordinary.y);
        await page.waitForTimeout(100);
        assert.match(received, /\x1b\[</, 'ordinary terminal input keeps mouse reporting');
    }
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): direct and real-tmux URL/OSC 8 activation, phone tap, preserved fragments, no duplicate remote clicks, swipe and unsafe protocol guards, normal mouse reporting.`);
} finally { await browser.close(); }
