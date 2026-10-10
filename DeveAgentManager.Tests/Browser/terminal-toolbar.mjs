const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// The terminal toolbar: Stop, Restart, VS Code, the Runner desktop, Sharing and the Conversation are only in the ⋮ menu;
// the other buttons stay while they fit and move into the menu one by one, in priority order, at every width.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const agentId = '00000000-0000-0000-0000-000000000001', sessionId = '00000000-0000-0000-0000-000000000010';
const now = new Date().toISOString();
// What leaves the toolbar first (AgentTerminal.ToolbarOverflow), with the menu entry each one becomes.
const priority = [
    ['hint', null], ['avatar', null], ['switch-label', null], ['viewers', 'Screens (2)'], ['quick', 'Quick commands'],
    ['images', 'Shared images (3)'], ['files', 'Shared files (1)'], ['awake', 'Keep screen awake'], ['limit', 'Schedule continue'], ['sessions', 'Switch session'],
];
const selectors = { hint: '.terminal-hint', avatar: '.terminal-avatar', 'switch-label': '.switch-chip-label' };
const fixed = ['Conversation', 'Sharing', 'Runner desktop', 'VS Code', 'Restart the harness', 'Stop the agent'];

async function run(touch) {
    const context = await browser.newContext({ viewport: { width: 1600, height: 900 }, hasTouch: touch, isMobile: false });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const agent = { id: agentId, runnerId: agentId, ownerId: agentId, ownerName: 'Test user', access: 'Owner', runnerName: 'runner-01', runnerState: 'Ready',
        runnerHarnessIds: ['claude', 'codex'], runnerToolIds: ['vscode'], name: 'deveagentmanager', harnessId: 'claude', workingDirectory: '/work/deveagentmanager',
        state: 'Running', accountName: 'Xebia', loginOwnerId: agentId, loginOwnerName: 'Test user', createdAt: now, updatedAt: now, iconVersion: 1,
        working: true, runnerKind: 'Desktop' };
    const session = { id: sessionId, ownerId: agentId, ownerName: 'Test user', access: 'Owner', title: 'Plan Windows runner agent', harnessId: 'claude', messageCount: 3, createdAt: now, updatedAt: now };
    const images = [1, 2, 3].map(i => ({ id: `00000000-0000-0000-0000-00000000002${i}`, sessionId, fileName: `shot-${i}.png`, caption: `Shot ${i}`, contentType: 'image/png', bytes: 2048, createdAt: now }));
    const files = [{ id: '00000000-0000-0000-0000-000000000031', sessionId, fileName: 'app-release.apk', caption: '', bytes: 4096, sha256: 'a'.repeat(64), createdAt: now }];
    const viewers = [{ tty: '/dev/pts/99', columns: 120, rows: 40, connectedAt: now, lastActiveAt: now }, { tty: '/dev/pts/7', columns: 80, rows: 24, connectedAt: now, lastActiveAt: now, watching: true }];
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        if (path.endsWith('/icon')) return route.fulfill({ status: 404 });
        const body = path === '/api/info' ? info : path === `/api/agents/${agentId}` ? agent : path === `/api/agents/${agentId}/session` ? session
            : path === `/api/sessions/${sessionId}/images` ? images : path === `/api/sessions/${sessionId}/files` ? files
            : path === `/api/agents/${agentId}/viewers` ? viewers : path === `/api/agents/${agentId}/commands` ? [{ command: '/compact', label: 'Compact the conversation' }]
            : path === '/api/agents' ? [agent] : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') ? null : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    await page.goto(base + `/agents/${agentId}`);
    const toolbar = page.locator('.terminal-toolbar');
    await toolbar.locator('.terminal-name').waitFor({ timeout: 60000 });
    await toolbar.locator('[data-toolbar="files"]').waitFor({ timeout: 60000 });
    // Leaving the home page mid-start aborts its downloads ("Failed to fetch"); only this page's errors count.
    errors.length = 0;

    const state = () => toolbar.evaluate((bar, { priority, selectors }) => {
        const shown = element => !!element && !element.hidden && element.getClientRects().length > 0;
        const name = bar.querySelector('.terminal-name');
        return {
            overflow: bar.scrollWidth - bar.clientWidth,
            pageOverflow: document.documentElement.scrollWidth - innerWidth,
            name: { width: name.clientWidth, natural: name.scrollWidth },
            items: Object.fromEntries(priority.map(([key]) => {
                const element = bar.querySelector(selectors[key] ?? `[data-toolbar="${key}"]`);
                return [key, !element ? 'absent' : element.hidden ? 'menu' : shown(element) ? 'toolbar' : 'unavailable'];
            })),
            fixedInToolbar: [...bar.querySelectorAll('button, a')].filter(shown).map(b => b.getAttribute('aria-label')).filter(label => /^(Stop|Restart|Conversation|VS Code|Sharing|Runner desktop)$/.test(label ?? '')),
            always: ['Back to agents', 'More actions'].filter(label => shown(bar.querySelector(`[aria-label="${label}"]`))).length
                + (shown(bar.querySelector('.switch-chip')) ? 1 : 0),
        };
    }, { priority, selectors: { ...selectors, hint: selectors.hint } });
    const more = page.locator('.terminal-toolbar button[aria-label="More actions"]');
    const openMenus = page.locator('.mud-popover-open .mud-menu-list');
    // Escape doesn't close MudBlazor menus here; ⋮ toggles the menu (and its submenus) closed.
    const closeMenu = async () => {
        if (await openMenus.count() > 0) await more.click();
        await openMenus.first().waitFor({ state: 'detached' });
    };
    const menuEntries = async () => {
        await more.click();
        const menu = page.locator('.mud-popover-open .mud-menu-list').last();
        await menu.getByText('Stop the agent', { exact: true }).waitFor();
        // Entries whose label loads (counts) settle within a moment.
        await page.waitForTimeout(400);
        const texts = (await menu.locator('.mud-menu-item').allTextContents()).map(text => text.trim());
        const children = await menu.evaluate(list => [...list.querySelectorAll('.mud-menu-item, hr, .mud-divider')].map(el => el.matches('hr, .mud-divider') ? '---' : el.textContent.trim()));
        await closeMenu();
        return { texts, children };
    };

    const widths = [1600, 1366, 1280, 1100, 1024, 960, 900, 820, 768, 700, 600, 540, 480, 412, 390, 360, 320];
    let previousShown = null;
    for (const width of widths) {
        await page.setViewportSize({ width, height: 900 });
        await page.waitForTimeout(150);
        const s = await state();
        const label = `${engine}${touch ? ' touch' : ''} at ${width}px`;
        assert.ok(s.overflow <= 1, `${label}: the toolbar overflows by ${s.overflow}px`);
        assert.ok(s.pageOverflow <= 0, `${label}: the page scrolls sideways`);
        assert.ok(s.name.width >= Math.min(72, s.name.natural) - 1, `${label}: the name is squeezed to ${s.name.width}px`);
        assert.deepEqual(s.fixedInToolbar, [], `${label}: Stop, Restart, VS Code, the desktop, Sharing and the Conversation belong in the menu`);
        assert.equal(s.always, 3, `${label}: back, the harness button and ⋮ always stay`);
        // Items leave in priority order: what went to the menu is always a prefix of the priority list.
        const order = priority.map(([key]) => s.items[key]).filter(where => where === 'menu' || where === 'toolbar');
        const firstShown = order.indexOf('toolbar');
        assert.ok(firstShown === -1 || order.slice(firstShown).every(where => where === 'toolbar'), `${label}: out of priority order ${JSON.stringify(s.items)}`);
        const hidden = order.filter(where => where === 'menu').length;
        const shown = priority.map(([key]) => key).filter(key => s.items[key] === 'toolbar');
        assert.ok(!previousShown || shown.every(key => previousShown.includes(key)), `${label}: a narrower toolbar must not show a button the wider one didn't`);
        previousShown = shown;
        if (width === 1600) assert.equal(hidden, 0, `${label}: everything fits on a wide screen ${JSON.stringify(s.items)}`);
        if (width >= 390) assert.equal(s.items.sessions, 'toolbar', `${label}: the session button stays on phones`);

        // Everything that left the toolbar is in the menu, and nothing else is; the fixed actions always are, Restart and Stop last.
        if ([1600, 1100, 900, 768, 600, 390, 320].includes(width)) {
            const { texts, children } = await menuEntries();
            for (const [key, entry] of priority.filter(([, entry]) => entry)) {
                const inMenu = texts.some(text => text.startsWith(entry));
                assert.equal(inMenu, s.items[key] === 'menu', `${label}: ${key} is ${s.items[key]} but ${inMenu ? 'is' : 'is not'} in the menu (${texts.join(' | ')})`);
            }
            for (const entry of fixed) assert.ok(texts.includes(entry), `${label}: ${entry} missing from the menu`);
            assert.deepEqual(children.slice(-3), ['---', 'Restart the harness', 'Stop the agent'], `${label}: Restart and Stop come last, apart`);
            await page.screenshot({ path: `/tmp/dam-toolbar-${engine}${touch ? '-touch' : ''}-${width}.png`, clip: { x: 0, y: 0, width, height: 60 } });
        }
    }

    // The entries work: a dialog, the screens submenu, and (with a mouse) the quick commands submenu.
    await page.setViewportSize({ width: 360, height: 800 });
    await page.waitForTimeout(150);
    await more.click();
    await page.getByRole('menuitem', { name: /^Shared images/ }).click();
    await page.getByRole('dialog').getByText('Shot 1').waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.getByRole('dialog').waitFor({ state: 'detached' });
    await more.click();
    await page.getByText('Screens (2)', { exact: true }).click();
    await page.getByText('Screens attached to this agent').waitFor();
    assert.ok(await page.getByText('Other screen').first().isVisible());
    await closeMenu();
    if (!touch) {
        await page.setViewportSize({ width: 760, height: 800 });
        await page.waitForTimeout(150);
        if ((await state()).items.quick === 'menu') {
            await more.click();
            await page.getByText('Quick commands', { exact: true }).click();
            await page.getByText('/compact · Compact the conversation').waitFor();
            await closeMenu();
        }
    }
    assert.deepEqual(errors, []);
    await context.close();
}

try {
    await run(false);
    await run(true);
    console.log(`Passed (${engine}): fixed actions only in the ⋮ menu, Restart and Stop last; every width from 1600 to 320px fits without squeezing the name, hiding buttons in priority order with matching menu entries that work.`);
} finally {
    await browser.close();
}
