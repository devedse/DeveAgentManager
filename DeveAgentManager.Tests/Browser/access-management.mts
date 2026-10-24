// Disposable Manager; APIs below model shared logins and each access surface.
import assert from 'node:assert/strict';
const { chromium } = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const browser = await chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const now = new Date().toISOString();
try {
    const theme = (process.env.DAM_TEST_COLOR_SCHEME ?? 'light') as 'light' | 'dark';
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: theme });
    await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
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
        workingDirectory: '/work/test-project', state: 'Running', accountId: id(710), accountName: 'Personal', accountUsername: 'alice',
        runningAccountId: id(711), runningAccountName: 'Shared', runningAccountUsername: 'bob', createdAt: now, updatedAt: now, canUseRunner: true } as { id: string; name: string; ownerId: any; ownerName: string; access: string; runnerId: string; runnerName: string; runnerState: string; runnerHarnessIds: string[]; runnerToolIds: any[]; harnessId: string; workingDirectory: string; state: string; accountId: string; accountName: string; accountUsername: string; runningAccountId: string; runningAccountName: string; runningAccountUsername: string; createdAt: string; updatedAt: string; canUseRunner: boolean; } & Record<string, any>;
    const stopped = { ...agent, id: id(703), name: 'stopped-agent', state: 'Stopped', runningAccountId: null };
    const defaultAgent = { ...stopped, id: id(704), name: 'default-agent', accountId: null, accountName: null, accountUsername: me.username };
    const agents = [agent, stopped, defaultAgent];
    runner.agents = agents;
    const accounts = [
        { id: id(710), harnessId: 'codex', name: 'Personal', createdByUsername: 'alice', access: 'Manage', isDefault: true },
        { id: id(711), harnessId: 'codex', name: 'Shared', createdByUsername: 'bob', access: 'Use' },
        { id: id(712), harnessId: 'grok', name: 'Personal', createdByUsername: 'bob', access: 'Use' },
        { id: id(713), harnessId: 'codex', name: 'Admin view', createdByUsername: 'carol', access: 'None' },
        { id: id(714), harnessId: 'codex', name: 'Disabled', createdByUsername: 'carol', access: 'Use' }
    ].map(a => ({ ...a, kind: 'ApiKey', enabled: true, canManage: true, version: 1, createdAt: now, updatedAt: now }));
    accounts.find(a => a.id === id(714)).enabled = false;
    const other = { id: id(720), username: 'bob', displayName: 'Bob' };
    let grants = [{ userId: me.id, username: me.username, displayName: 'Admin', access: 'Manage', disabled: false },
        { userId: other.id, username: other.username, displayName: other.displayName, access: 'Use', disabled: false }];
    const shares = [{ userId: other.id, username: 'bob', displayName: 'Bob', access: 'Read', grantedAt: now }];
    const session = { id: id(730), title: 'Test conversation', ownerId: me.id, ownerName: 'Admin', access: 'Owner', harnessId: 'codex',
        agentId: agent.id, workspaceAgentId: agent.id, workspaceAccess: 'Owner', agent, messageCount: 0, createdAt: now, updatedAt: now };
    const mutations = [], accountRequests = [];
    let moveProcesses = [], processGate = null, processFailure = false, agentReads = 0;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname, method = route.request().method();
        if (method === 'GET' && path === '/api/agents/' + agent.id) agentReads++;
        if (method === 'GET' && (path === '/api/accounts' || path === '/api/accounts/all')) accountRequests.push(path);
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (path.endsWith('/move/processes')) {
            if (processGate) await processGate;
            return processFailure
                ? route.fulfill({ status: 500, json: { detail: 'Test-only process lookup failure' } })
                : route.fulfill({ json: moveProcesses });
        }
        if (method !== 'GET') {
            mutations.push({ path, body: route.request().postDataJSON() });
            if (path.endsWith('/grants/' + other.id)) grants = grants.map(g => g.userId === other.id ? { ...g, access: 'Manage' } : g);
            return route.fulfill({ status: 204 });
        }
        const body = path === '/api/info' ? info : (path === '/api/accounts' || path === '/api/accounts/all') ? accounts
            : path.endsWith('/grants') ? grants : path.endsWith('/members') ? [{ ...shares[0], access: 'Use' }]
            : path.endsWith('/shares') ? shares : path === '/api/users/directory' ? [other]
            : path === '/api/runners/' + runner.id + '/password' ? { password: 'test-only-long-runner-password-0123456789' }
            : path === '/api/runners' ? [runner, target] : path === '/api/runners/' + runner.id ? runner
            : path === '/api/agents' ? agents : path === '/api/agents/' + agent.id ? agent
            : path === '/api/sessions' ? [session] : path === '/api/sessions/' + session.id ? session
            : path.endsWith('/session') ? session : path.endsWith('/limit/automatic') ? {enabled:false,sent:0} : path.endsWith('/limit') ? null
            : path.endsWith('/conversation') || path.endsWith('/content') ? { version: 1, entries: [], sources: [] } : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    const assertIdentityColors = async identity => {
        const colors = await identity.evaluate(node => ({
            username: getComputedStyle(node.querySelector('.account-username')).color,
            name: getComputedStyle(node.querySelector('.account-label-name')).color,
            separator: getComputedStyle(node.querySelector('.account-separator')).color
        }));
        assert.equal(new Set(Object.values(colors)).size, 3, `username, name and separator have distinct colors in ${theme} mode`);
    };
    for (const width of [1280, 320]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + '/agents');
        const runningRow = page.locator('.agent-row').filter({ has: page.locator('.list-title', { hasText: 'test-agent' }) });
        await runningRow.locator('.agent-account').getByText('Account: bob · Shared', { exact: true }).waitFor();
        await assertIdentityColors(runningRow.locator('.account-identity'));
        await page.locator('.agent-row').filter({ hasText: 'stopped-agent' }).locator('.agent-account')
            .getByText('Account on start: alice · Personal', { exact: true }).waitFor();
        await page.locator('.agent-row').filter({ hasText: 'default-agent' }).locator('.agent-account')
            .getByText(`Account on start: ${me.username} · Default`, { exact: true }).waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `account labels fit ${width}px`);
        await page.screenshot({ path: `/tmp/dam-account-labels-${theme}-${width}.png` });
    }
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base + '/agents/' + agent.id);
    await page.locator('.switch-chip').waitFor().catch(async error => { console.error(errors, await page.locator('body').innerText()); throw error; });
    await page.locator('.switch-chip').click();
    const menu = page.locator('.switch-menu:visible');
    await menu.getByRole('button', { name: /bob · Shared/ }).waitFor().catch(async error => { console.error('MENU', await page.locator('body').innerText(), errors); await page.screenshot({path:'/tmp/dam-picker-debug.png'}); throw error; });
    assert.equal(await menu.getByRole('button', { name: /bob · Personal/ }).isDisabled(), true, 'Grok login remains visible when the runner lacks Grok');
    assert.equal(await menu.getByRole('button', { name: /carol · Admin view/ }).count(), 0, 'administration-only logins do not appear in the switcher');
    assert.equal(await menu.getByRole('button', { name: /carol · Disabled/ }).count(), 0, 'disabled logins do not appear in the switcher');
    assert.ok(accountRequests.length > 0 && accountRequests.every(path => path === '/api/accounts'), 'switcher requests personal visibility, not the administrator catalog');
    await menu.getByRole('link', { name: 'Install Grok on test-runner' }).waitFor();
    const lefts = await menu.locator('.switch-option-label').evaluateAll(nodes => nodes.map(n => Math.round(n.getBoundingClientRect().left)));
    assert.equal(new Set(lefts).size, 1, 'all account labels share an aligned left edge');
    await assertIdentityColors(menu.getByRole('button', { name: /alice · Personal/ }).locator('.account-identity'));
    await assertIdentityColors(menu.getByRole('button', { name: /bob · Shared/ }).locator('.account-identity'));
    await assertIdentityColors(page.locator('.switch-chip .account-identity'));
    await page.locator('.switch-chip .account-identity').getByText('bob', { exact: true }).waitFor();
    await page.screenshot({ path: '/tmp/dam-account-picker.png' });
    await page.setViewportSize({ width: 320, height: 900 });
    await page.goto(base + '/agents/' + agent.id);
    await page.locator('.switch-chip').click();
    await menu.getByRole('button', { name: /alice · Personal/ }).waitFor();
    const menuBounds = await menu.boundingBox();
    await page.screenshot({ path: `/tmp/dam-account-picker-${theme}-320.png` });
    assert.ok(menuBounds.x >= 0 && menuBounds.x + menuBounds.width <= 321, `colored switcher labels fit a 320px phone: ${JSON.stringify(menuBounds)}`);
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base + '/harness-logins');
    await page.locator('.account-row').filter({ hasText: 'carol · Admin view' }).waitFor();
    assert.ok(accountRequests.includes('/api/accounts/all'), 'administration-only logins stay on the Harness logins page');
    await page.locator('.account-row').filter({ hasText: 'alice · Personal' }).getByRole('button', { name: 'Manage access' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.locator('.access-person').filter({ hasText: 'Bob (bob)' }).waitFor();
    await dialog.getByLabel('Access for bob', {exact:true}).click();
    await page.getByRole('menuitem', { name: 'Change to Manage', exact: true }).click();
    await dialog.locator('.access-person').filter({ hasText: 'Bob (bob)' }).getByText('Manage', { exact: true }).waitFor();
    assert.ok(mutations.some(m => m.path.endsWith('/grants/' + other.id) && m.body.access === 'Manage'));
    await dialog.getByRole('button', { name: 'Done', exact: true }).click();
    for (const width of [1920, 1280, 390, 320]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + '/runners/' + runner.id);
        await page.locator('.access-list').getByText('Bob (bob)', { exact: true }).waitFor();
        await page.locator('.access-list').getByRole('button', { name: 'Add user', exact: true }).waitFor();
        const password = page.locator('.runner-password');
        await password.getByRole('button', { name: 'Show', exact: true }).click();
        await password.getByText('test-only-long-runner-password-0123456789', { exact: true }).waitFor();
        const layout = await password.evaluate(row => {
            const value = row.querySelector('.operation-description').getBoundingClientRect();
            const buttons = [...row.querySelectorAll('button')].map(b => b.getBoundingClientRect());
            const bounds = row.getBoundingClientRect();
            return { fits: value.left >= bounds.left && value.right <= bounds.right,
                overlaps: buttons.some(b => value.left < b.right && value.right > b.left && value.top < b.bottom && value.bottom > b.top) };
        });
        assert.ok(layout.fits && !layout.overlaps, `revealed runner password fits without overlapping controls at ${width}px`);
        await password.getByRole('button', { name: 'Hide', exact: true }).click();
        assert.equal(await password.getByText('test-only-long-runner-password-0123456789', { exact: true }).count(), 0);
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
    await page.getByRole('menuitem', { name: 'Move project to another runner…', exact: true }).click();
    await page.getByRole('dialog').getByRole('combobox', { name: 'Runner', exact: true }).click();
    await page.getByRole('option', { name: target.name, exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Move project', exact: true }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    assert.ok(mutations.some(m => m.path === '/api/agents/' + agent.id + '/move' && m.body.runnerId === target.id));
    const vite = { identity: { pid: 642, startTicks: 123456, bootId: id(900) }, name: 'vite.js (node)', workingDirectory: '/work/test-project' };
    const worker = { identity: { pid: 643, startTicks: 123457, bootId: id(900) }, name: 'python3', workingDirectory: '/work/test-project/a-folder-with-a-long-name-that-must-wrap-on-phones' };
    const openMove = async () => {
        await page.getByLabel('More actions', { exact: true }).click();
        await page.getByRole('menuitem', { name: 'Move project to another runner…', exact: true }).click();
        await page.getByRole('dialog').getByRole('combobox', { name: 'Runner', exact: true }).click();
        await page.getByRole('option', { name: target.name, exact: true }).click();
    };
    moveProcesses = [vite]; processFailure = true;
    let releaseProcesses;
    processGate = new Promise(resolve => { releaseProcesses = resolve; });
    await page.setViewportSize({ width: 320, height: 900 });
    let moveDialog;
    try {
        await openMove();
        moveDialog = page.getByRole('dialog');
        await moveDialog.locator('[role="status"]', { hasText: 'Checking background processes' }).waitFor({ state: 'attached' });
        assert.equal(await moveDialog.getByText('No blocking processes.').count(), 0);
        assert.equal(await moveDialog.getByRole('button', { name: 'Move project', exact: true }).isDisabled(), true);
    } finally {
        processGate = null;
        releaseProcesses();
    }
    await moveDialog.getByText('Test-only process lookup failure', { exact: true }).waitFor();
    processFailure = false;
    await moveDialog.getByRole('button', { name: 'Retry', exact: true }).click();
    await moveDialog.getByText('vite.js (node)', { exact: true }).waitFor();
    const movesBeforeCancel = mutations.filter(m => m.path.endsWith('/move')).length;
    await moveDialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await moveDialog.waitFor({ state: 'hidden' });
    assert.equal(mutations.filter(m => m.path.endsWith('/move')).length, movesBeforeCancel, 'cancel never authorizes a stop');
    await openMove();
    moveDialog = page.getByRole('dialog');
    await moveDialog.getByText('vite.js (node)', { exact: true }).waitFor();
    moveProcesses = [vite, worker];
    await moveDialog.getByRole('button', { name: 'Stop processes and move', exact: true }).click();
    await moveDialog.getByText('python3', { exact: true }).waitFor();
    assert.equal(mutations.filter(m => m.path.endsWith('/move')).length, movesBeforeCancel, 'new blockers require another review');
    const bounds = await moveDialog.boundingBox();
    assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= 321, 'process review dialog fits a 320px phone');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.waitForFunction(() => [...document.querySelectorAll('[role="dialog"] button')]
        .some(button => button.textContent.trim() === 'Stop processes and move' && !(button as HTMLButtonElement).disabled));
    await moveDialog.screenshot({ path: '/tmp/dam-move-processes-320.png' });
    await moveDialog.getByRole('button', { name: 'Stop processes and move', exact: true }).click();
    await moveDialog.waitFor({ state: 'hidden' });
    const approved = mutations.filter(m => m.path.endsWith('/move')).at(-1);
    assert.deepEqual(approved.body.stopProcesses, [vite.identity, worker.identity], 'only reviewed identities are approved');

    // A disposable SignalR connection lets the open dialog and list observe the same live progress events.
    let notify, connected;
    const connection = new Promise(resolve => { connected = resolve; });
    await context.routeWebSocket('**/hubs/live?**', socket => {
        socket.onMessage(data => {
            for (const frame of data.toString().split('\x1e').filter(Boolean)) {
                const message = JSON.parse(frame);
                if (message.protocol) { socket.send('{}\x1e'); connected(); }
                else if (message.type === 6) socket.send('{"type":6}\x1e');
            }
        });
        notify = () => socket.send(JSON.stringify({ type: 1, target: 'changed', arguments: [{ kind: 'AgentChanged', runnerId: runner.id, agentId: agent.id }] }) + '\x1e');
    });
    agent.state = 'Moving'; agent.movePending = true;
    agent.stateMessage = 'Uploading to destination-runner…';
    agent.moveDetails = { id: id(777).replaceAll('-', ''), sourceRunner: runner.name, targetRunner: target.name, startedAt: now,
        outcome: 'In progress', progress: { stage: 'Uploading to destination-runner', currentFile: '/work/test-project/screenshots/a-very-long-screenshot-filename-for-phone-layout-checks.png', bytesCompleted: 4194304, totalBytes: 8388608 } };
    const stepNames = [['check','Check runners and destination'],['stop','Stop the source agent'],['history','Verify session history and attachments'],
        ['pack','Find files and prepare the archive'],['download','Download from the source'],['upload','Upload to the destination'],
        ['restore','Restore and verify destination files'],['install','Install files and commit the destination'],['start','Start the destination agent'],
        ['cleanup','Remove source files'],['target-cleanup','Clean destination after a failure'],['recovery','Restore the agent on the source']];
    agent.moveDetails.steps = stepNames.map(([id,title], index) => ({ id, title, status: index < 5 ? 'Completed' : index === 5 ? 'Running' : 'Pending',
        startedAt: index <= 5 ? new Date(Date.parse(now)-10000).toISOString() : null,
        finishedAt: index < 5 ? new Date(Date.parse(now)-9000).toISOString() : null,
        detail: id === 'upload' ? 'Streams the verified archive to the destination.' : 'Test-only recorded step details.',
        stages: id === 'upload' ? [agent.moveDetails.progress] : [] }));
    await page.goto(base + '/agents');
    await connection;
    await page.getByRole('button', { name: 'Explain test-agent status: Moving', exact: true }).waitFor();
    const inline = page.locator('.agent-row .agent-move-progress');
    await inline.getByText('50%', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'inline move progress fits a 320px phone');
    await page.getByRole('button', { name: 'Explain test-agent status: Moving', exact: true }).click();
    await page.getByRole('dialog').getByText('Move details', { exact: true }).waitFor();
    assert.equal(await page.locator('.move-step').count(), 12, 'all planned steps are visible');
    assert.equal(await page.locator('.move-step-completed').count(), 5);
    assert.equal(await page.locator('.move-step-running').count(), 1);
    await page.locator('[data-step-id="check"] .move-step-duration').getByText('1.0s', { exact: true }).waitFor();
    const uploadStep = page.locator('[data-step-id="upload"]');
    await uploadStep.locator('summary').focus();
    await page.keyboard.press('Enter');
    await uploadStep.getByText('Streams the verified archive to the destination.', { exact: true }).waitFor();
    const elapsed = await uploadStep.locator('.move-step-duration').innerText();
    const reads = agentReads;
    await page.waitForFunction(previous => document.querySelector('[data-step-id="upload"] .move-step-duration')?.textContent !== previous, elapsed);
    assert.equal(agentReads, reads, 'elapsed time updates locally without polling agent metadata');
    const latest = page.getByRole('dialog').getByRole('region', { name: 'Latest move' });
    await latest.getByText('test-runner → destination-runner', { exact: true }).waitFor();
    await latest.getByText(id(777).replaceAll('-', ''), { exact: true }).waitFor();
    await latest.getByText('50%', { exact: true }).waitFor();
    agent.moveDetails.progress = { stage: 'Finding session files and attachments', currentFile: '/work/test-project/native-session.jsonl', bytesCompleted: 16777216, totalBytes: null };
    notify();
    await latest.getByText('Processed 16 MiB', { exact: true }).waitFor();
    await latest.getByText('/work/test-project/native-session.jsonl', { exact: true }).waitFor();
    agent.moveDetails.progress = { stage: 'Uploading to destination-runner', currentFile: '/work/test-project/shared-documents/design.pdf', bytesCompleted: 6291456, totalBytes: 8388608 };
    agent.moveDetails.steps = agent.moveDetails.steps.map(step => step.id === 'upload' ? { ...step, stages: [agent.moveDetails.progress] } : step);
    notify();
    await latest.getByText('75%', { exact: true }).waitFor();
    await latest.getByText('/work/test-project/shared-documents/design.pdf', { exact: true }).waitFor();
    await uploadStep.getByText('/work/test-project/shared-documents/design.pdf', { exact: true }).waitFor();
    for (const width of [320, 1280]) {
        await page.setViewportSize({ width, height: width === 1280 ? 1400 : 900 });
        const layout = await latest.evaluate(node => ({ width: node.scrollWidth, available: node.clientWidth }));
        assert.ok(layout.width <= layout.available + 1, 'move details wrap long paths');
        const stepsLayout = await page.getByRole('region', { name: 'Move steps' }).evaluate(node => ({ width: node.scrollWidth, available: node.clientWidth }));
        assert.ok(stepsLayout.width <= stepsLayout.available + 1, 'expanded steps fit narrow phones');
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        await page.getByRole('dialog').screenshot({ path: `/tmp/dam-move-progress-${width}.png` });
    }
    agent.state = 'Running'; agent.movePending = false; agent.stateMessage = null;
    agent.activity = { status: 'Ready', reason: 'Ready for a prompt.', currentTurnCompleted: false };
    agent.moveDetails = { ...agent.moveDetails, outcome: 'Completed', completedAt: now, message: 'Source files removed.', progress: null };
    agent.moveDetails.steps = agent.moveDetails.steps.map(step => ({ ...step, status: ['target-cleanup','recovery'].includes(step.id) ? 'Skipped' : 'Completed',
        startedAt: step.startedAt ?? now, finishedAt: now }));
    notify();
    await latest.getByText('Completed', { exact: true }).waitFor();
    assert.equal(await latest.locator('[role="progressbar"]').count(), 0, 'completion does not leave a running progress indicator');
    assert.equal(await page.locator('.move-step-completed').count(), 10);
    assert.equal(await page.locator('.move-step-skipped').count(), 2);
    await page.getByRole('dialog').locator('.mud-dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('button', { name: 'Explain test-agent status: Ready', exact: true }).click();
    await page.getByRole('dialog').getByRole('region', { name: 'Latest move' }).getByText('Completed', { exact: true }).waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'View move details', exact: true }).click();
    await page.getByRole('dialog').getByText('Move details', { exact: true }).waitFor();
    assert.equal(await page.locator('.move-step-completed').count(), 10, 'Ready can reopen the recorded step timeline');
    await page.getByRole('dialog').locator('.mud-dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
    agent.moveDetails = { ...agent.moveDetails, outcome: 'Failed; source retained', message: 'Test-only destination unavailable.' };
    agent.moveDetails.steps = agent.moveDetails.steps.map(step => step.id === 'upload' ? { ...step, status: 'Failed', error: 'Test-only destination unavailable.' } : step);
    notify();
    await page.getByRole('button', { name: 'Explain test-agent status: Ready', exact: true }).click();
    await page.getByRole('dialog').getByRole('region', { name: 'Latest move' }).getByText('Failed; source retained', { exact: true }).waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'View move details', exact: true }).click();
    await page.locator('[data-step-id="upload"] summary').click();
    await page.locator('.move-step-failed .move-step-error').getByText('Test-only destination unavailable.', { exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log('Passed: aligned logins, access controls, password layouts, move process review and live move progress with retained success/failure results.');
} finally { await browser.close(); }
