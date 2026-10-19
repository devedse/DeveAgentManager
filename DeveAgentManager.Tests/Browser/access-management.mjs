// Disposable Manager; APIs below model shared logins and each access surface.
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const browser = await chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const now = new Date().toISOString();
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const me = await (await context.request.get(base + '/api/me')).json();
    const info = await (await context.request.get(base + '/api/info')).json();
    const runner = { id: id(700), name: 'test-runner', ownerId: me.id, ownerName: 'Admin', access: 'Owner', state: 'Ready',
        kind: 'Headless', providerId: 'existing', host: 'runner.test', username: 'runneruser', harnessIds: ['codex','claude','pi'], toolIds: [], agents: [], createdAt: now, updatedAt: now };
    const target = { ...runner, id: id(702), name: 'destination-runner', agents: [] };
    const agent = { id: id(701), name: 'test-agent', ownerId: me.id, ownerName: 'Admin', access: 'Owner', runnerId: runner.id,
        runnerName: runner.name, runnerState: 'Ready', runnerHarnessIds: runner.harnessIds, runnerToolIds: [], harnessId: 'codex',
        workingDirectory: '/work/test-project', state: 'Running', accountId: id(710), runningAccountId: id(710), createdAt: now, updatedAt: now };
    runner.agents = [agent];
    const accounts = [
        { id: id(710), harnessId: 'codex', name: 'Personal', createdByUsername: 'alice', access: 'Manage', isDefault: true },
        { id: id(711), harnessId: 'codex', name: 'Shared', createdByUsername: 'bob', access: 'Use' },
        { id: id(712), harnessId: 'grok', name: 'Personal', createdByUsername: 'bob', access: 'Use' },
        { id: id(713), harnessId: 'codex', name: 'Admin view', createdByUsername: 'carol', access: 'None' }
    ].map(a => ({ ...a, kind: 'ApiKey', enabled: true, canManage: true, version: 1, createdAt: now, updatedAt: now }));
    const other = { id: id(720), username: 'bob', displayName: 'Bob' };
    let grants = [{ userId: me.id, username: me.username, displayName: 'Admin', access: 'Manage', disabled: false },
        { userId: other.id, username: other.username, displayName: other.displayName, access: 'Use', disabled: false }];
    const shares = [{ userId: other.id, username: 'bob', displayName: 'Bob', access: 'Read', grantedAt: now }];
    const session = { id: id(730), title: 'Test conversation', ownerId: me.id, ownerName: 'Admin', access: 'Owner', harnessId: 'codex',
        agentId: agent.id, workspaceAgentId: agent.id, workspaceAccess: 'Owner', agent, messageCount: 0, createdAt: now, updatedAt: now };
    const mutations = [];
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname, method = route.request().method();
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (method !== 'GET') {
            mutations.push({ path, body: route.request().postDataJSON() });
            if (path.endsWith('/grants/' + other.id)) grants = grants.map(g => g.userId === other.id ? { ...g, access: 'Manage' } : g);
            return route.fulfill({ status: 204 });
        }
        const body = path === '/api/info' ? info : (path === '/api/accounts' || path === '/api/accounts/all') ? accounts
            : path.endsWith('/grants') ? grants : path.endsWith('/members') ? [{ ...shares[0], access: 'Use' }]
            : path.endsWith('/shares') ? shares : path === '/api/users/directory' ? [other]
            : path === '/api/runners' ? [runner, target] : path === '/api/runners/' + runner.id ? runner
            : path === '/api/agents' ? [agent] : path === '/api/agents/' + agent.id ? agent
            : path === '/api/sessions' ? [session] : path === '/api/sessions/' + session.id ? session
            : path.endsWith('/session') ? session : path.endsWith('/limit/automatic') ? {enabled:false,sent:0} : path.endsWith('/limit') ? null
            : path.endsWith('/conversation') || path.endsWith('/content') ? { version: 1, entries: [], sources: [] } : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    await page.goto(base + '/agents/' + agent.id);
    await page.locator('.switch-chip').waitFor().catch(async error => { console.error(errors, await page.locator('body').innerText()); throw error; });
    await page.locator('.switch-chip').click();
    const menu = page.locator('.switch-menu:visible');
    await menu.getByRole('button', { name: /bob · Shared/ }).waitFor().catch(async error => { console.error('MENU', await page.locator('body').innerText(), errors); await page.screenshot({path:'/tmp/dam-picker-debug.png'}); throw error; });
    assert.equal(await menu.getByRole('button', { name: /bob · Personal/ }).isDisabled(), true, 'Grok login remains visible when the runner lacks Grok');
    assert.equal(await menu.getByRole('button', { name: /carol · Admin view/ }).isDisabled(), true, 'admin visibility does not grant Use');
    await menu.getByRole('link', { name: 'Install Grok on test-runner' }).waitFor();
    const lefts = await menu.locator('.switch-option-label').evaluateAll(nodes => nodes.map(n => Math.round(n.getBoundingClientRect().left)));
    assert.equal(new Set(lefts).size, 1, 'all account labels share an aligned left edge');
    await page.screenshot({ path: '/tmp/dam-account-picker.png' });
    await page.keyboard.press('Escape');
    await page.goto(base + '/harness-logins');
    await page.locator('.account-row').filter({ hasText: 'alice · Personal' }).getByRole('button', { name: 'Manage access' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.locator('.access-person').filter({ hasText: 'Bob (bob)' }).waitFor();
    await dialog.getByLabel('Access for bob', {exact:true}).click();
    await page.getByRole('menuitem', { name: 'Change to Manage', exact: true }).click();
    await dialog.locator('.access-person').filter({ hasText: 'Bob (bob)' }).getByText('Manage', { exact: true }).waitFor();
    assert.ok(mutations.some(m => m.path.endsWith('/grants/' + other.id) && m.body.access === 'Manage'));
    await dialog.getByRole('button', { name: 'Done', exact: true }).click();
    for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + '/runners/' + runner.id);
        await page.locator('.access-list').getByText('Bob (bob)', { exact: true }).waitFor();
        await page.locator('.access-list').getByRole('button', { name: 'Add user', exact: true }).waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `runner access fits ${width}px`);
        await page.goto(base + '/agents/' + agent.id);
        await page.getByLabel('More actions', { exact: true }).click();
        await page.getByRole('menuitem', { name: 'Sharing', exact: true }).click();
        await page.getByRole('dialog').locator('.access-person').filter({ hasText: 'Bob (bob)' }).waitFor();
        await page.getByRole('dialog').getByRole('button', { name: 'Add user', exact: true }).waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `agent sharing fits ${width}px`);
        await page.screenshot({ path: `/tmp/dam-access-${width}.png` });
        await page.getByRole('dialog').getByRole('button', { name: 'Done', exact: true }).click();
        await page.goto(base + '/sessions/' + session.id);
        await page.getByLabel('More actions', { exact: true }).click();
        await page.getByRole('menuitem', { name: 'Sharing', exact: true }).click();
        await page.getByRole('dialog').locator('.access-person').filter({ hasText: 'Bob (bob)' }).waitFor();
        await page.getByRole('dialog').getByRole('button', { name: 'Add user', exact: true }).waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `conversation sharing fits ${width}px`);
        await page.getByRole('dialog').getByRole('button', { name: 'Done', exact: true }).click();
    }
    await page.goto(base + '/agents/' + agent.id);
    await page.getByLabel('More actions', { exact: true }).click();
    await page.getByRole('menuitem', { name: 'Move to another runner…', exact: true }).click();
    await page.getByRole('dialog').getByRole('combobox', { name: 'Runner', exact: true }).click();
    await page.getByRole('option', { name: target.name, exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Move agent', exact: true }).click();
    assert.ok(mutations.some(m => m.path === '/api/agents/' + agent.id + '/move' && m.body.runnerId === target.id));
    assert.deepEqual(errors, []);
    console.log('Passed: creator-prefixed aligned logins, explicit Use rights, four consistent access surfaces, permission updates, mobile layouts and move selection.');
} finally { await browser.close(); }
