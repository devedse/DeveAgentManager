const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// An update restarts the Manager: the agent's terminal is told so, keeps saying so while the Manager is still down, and
// connects again by itself once it is back.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date().toISOString();
    const agent = { id, runnerId: id, ownerId: id, ownerName: 'Test user', access: 'Owner', runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'],
        runnerToolIds: [], name: 'restart-test', harnessId: 'claude', workingDirectory: '/work/example', state: 'Running', loginOwnerId: id, loginOwnerName: 'Test user', createdAt: now, updatedAt: now };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        const body = path === '/api/info' ? info : path === `/api/agents/${id}` ? agent : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 }
            : path.endsWith('/limit') || path.endsWith('/session') ? null : [];
        return route.fulfill({ json: body });
    });

    // 1: works, then the Manager says it is restarting; 2 and 3: the Manager is down; 4: it is back.
    let connections = 0;
    let restart;
    const restarted = new Promise(resolve => restart = resolve);
    await context.routeWebSocket('**/ws/terminal?**', ws => {
        const n = ++connections;
        if (n === 1) {
            ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' }));
            ws.send(Buffer.from('agent output before the update\r\n'));
            restarted.then(() => {
                ws.send(JSON.stringify({ type: 'closed', reason: 'restarting', message: 'The Manager is restarting. The terminal connects again once it is back.' }));
                ws.close();
            });
        } else if (n < 4) {
            ws.close();
        } else {
            ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/10' }));
            ws.send(Buffer.from('agent output after the update\r\n'));
        }
    });

    await page.goto(base + `/agents/${id}`);
    await page.locator('.xterm-rows').getByText('agent output before the update').waitFor({ timeout: 60000 });
    assert.equal(await page.locator('.terminal-overlay').count(), 0);
    restart();

    // While it is down, the terminal says why, not a bare "Connecting".
    const overlay = page.locator('.terminal-overlay');
    await overlay.getByText(/The Manager is restarting/).waitFor({ timeout: 5000 });
    const seen = new Set();
    for (let i = 0; i < 100 && connections < 4; i++) {
        if (await overlay.count()) seen.add((await overlay.innerText()).trim());
        await page.waitForTimeout(50);
    }
    assert.ok([...seen].every(text => text.includes('The Manager is restarting')), `overlay while down: ${[...seen].join(' | ')}`);

    // Back: connected again by itself.
    await page.locator('.xterm-rows').getByText('agent output after the update').waitFor({ timeout: 15000 });
    await overlay.waitFor({ state: 'detached', timeout: 5000 });
    assert.equal(connections, 4);
    console.log(`Passed (${engine}): after "restarting" the terminal keeps saying so while the Manager is down and reconnects by itself once it is back.`);
} finally {
    await browser.close();
}
