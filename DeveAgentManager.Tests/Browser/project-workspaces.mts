import assert from 'node:assert/strict';
// Projects and workspaces: grouping, the workspace dropdown with ▶/■, New workspace sizes, the conversation chooser,
// removing a workspace and the project page. Nothing starts, stops or resumes unless it is chosen.
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5109';
const browser = await playwright[process.env.DAM_TEST_BROWSER ?? 'chromium'].launch();
try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
    const page = await context.newPage();
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const me = await (await context.request.get(base + '/api/me')).json();
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
    const now = new Date().toISOString();
    const agent = (n, name, state, project = 50) => ({ id: id(n), runnerId: id(1), ownerId: me.id, ownerName: 'Test user', access: 'Owner',
        runnerName: 'test-runner', runnerState: 'Ready', runnerHarnessIds: ['pi'], runnerToolIds: [], name, harnessId: 'pi',
        workingDirectory: n === 10 || n === 20 ? `/work/${name}` : `/work/example-project.workspaces/${name}`,
        state, loginOwnerId: me.id, loginOwnerName: 'Test user', createdAt: now, updatedAt: now, projectId: id(project), projectName: project === 50 ? 'Example project' : 'Stopped project',
        isClone: n !== 10 && n !== 20, repositoryBranch: n === 10 || n === 20 ? 'main' : name, canUseRunner: true });
    const agents = [agent(10, 'example-project', 'Running'), agent(11, 'login-fix', 'Stopped'), agent(20, 'stopped-project', 'Stopped', 60), agent(21, 'old-task', 'Stopped', 60)];
    Object.assign(agents[1], { harnessId: 'claude', accountName: 'Work login', accountUsername: 'admin' });
    const session = (n, title, agentId, workspaceAgentId = id(10), folder = '/work/example-project') => ({ id: id(100 + n), ownerId: me.id, ownerName: 'Test user', access: 'Owner', title, harnessId: 'pi', messageCount: 2,
        agentId, workspaceAgentId, workspaceAccess: 'Owner', projectId: id(50), workingDirectory: folder, repositoryBranch: 'main', createdAt: now, updatedAt: now, sourceSize: 0, savedSize: 0, totalSize: 0 });
    const sessions = [session(0, 'Current conversation', id(10)), session(1, 'Previous conversation', null),
        session(2, 'Removed workspace chat', null, null, '/work/example-project.workspaces/old-fix'),
        { ...session(3, 'Login fix chat', null, id(11), '/work/example-project.workspaces/login-fix'), repositoryBranch: 'login-fix' }];
    const writes: { path: string, body: any }[] = [];
    const login = (n, name) => ({ id: id(300 + n), harnessId: 'pi', name, kind: 'Subscription', isDefault: false, version: 1, agentCount: 0,
        createdAt: now, updatedAt: now, access: 'Use', enabled: true });
    const accounts = [login(1, 'Work login'), login(2, 'Personal login')];
    agents[0]['accountId'] = id(302);
    const runner = { id: id(1), name: 'test-runner', ownerId: me.id, ownerName: 'Test user', access: 'Owner', providerId: 'existing', kind: 'Headless', state: 'Ready',
        host: 'runner.test', cores: 4, memoryMb: 8192, diskGb: 64, agents, harnessIds: ['pi'], toolIds: [], createdAt: now, updatedAt: now };
    let slowAccounts = false;
    const estimates = { MainBranch: { bytes: 39845888, files: 1204, freeBytes: 15032385536, copyOnWrite: false, branch: 'main', canPull: true },
        LastCommit: { bytes: 39845888, files: 1204, freeBytes: 15032385536, copyOnWrite: false, branch: 'main', canPull: true },
        ExactCopy: { bytes: 21474836480, files: 51234, freeBytes: 15032385536, copyOnWrite: false, branch: 'main', canPull: true } };
    let slowWorkspace = false, failWorkspace = false, failCurrent = true, slowExact: Promise<void> | null = null, releaseExact = () => {};
    let changes = { path: agents[1].workingDirectory, exists: true, uncommitted: 3, sample: ['Program.cs', 'app.css', 'notes.md'], unpushed: 2, upstream: 'origin/main', branch: 'login-fix', conversations: 1 };
    await context.route('**/api/**', async route => {
        const url = new URL(route.request().url()), path = url.pathname, method = route.request().method();
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (path.endsWith('/workspaces/branches')) return route.fulfill({ json: { [id(11)]: 'switched-branch' } });
        if (method !== 'GET' && method !== 'HEAD') {
            // Viewing a terminal marks it seen; that is not a lifecycle command.
            if (!path.endsWith('/seen')) writes.push({ path, body: route.request().postDataJSON() });
            if (path.endsWith('/workspaces')) { agents.push(agent(12, 'workspace-2', 'Running')); return route.fulfill({ json: agents.at(-1) }); }
            return route.fulfill({ status: 204 });
        }
        if (path === '/api/agents' && slowWorkspace) await new Promise(resolve => setTimeout(resolve, 900));
        if (path === '/api/accounts' && slowAccounts) await new Promise(resolve => setTimeout(resolve, 900));
        if (path === '/api/agents' && failWorkspace) return route.fulfill({ status: 503, json: { error: 'Test-only workspace failure' } });
        if (path.endsWith('/workspaces/estimate')) {
            const start = url.searchParams.get('start');
            if (start === 'CurrentCode' && failCurrent) return route.fulfill({ status: 503, json: { detail: 'Test-only measurement failure' } });
            if (start === 'CurrentCode') return route.fulfill({ json: { ...estimates.LastCommit, bytes: 40894464, files: 1216 } });
            if (start === 'ExactCopy' && slowExact) await slowExact;
            return route.fulfill({ json: estimates[start] });
        }
        const selected = agents.find(a => path === `/api/agents/${a.id}`);
        const body = path === '/api/info' ? info : path === '/api/agents' ? agents : selected ? selected : path === '/api/runners' ? [runner]
            : path === `/api/runners/${id(1)}` ? runner : path === '/api/accounts' ? accounts
            : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null }
            : path.endsWith('/workspace/changes') ? changes : path.endsWith('/kept-folders')
                ? [{ path: '/work/example-project.workspaces/kept-fix', name: 'kept-fix', branch: 'kept-fix', removedAt: now }]
            : path.endsWith('/session') ? sessions[0] : path === '/api/sessions' ? sessions : [];
        return route.fulfill({ json: body });
    });

    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    const written = async (count: number) => { for (let i = 0; i < 100 && writes.length < count; i++) await page.waitForTimeout(50); };

    // Grouping and the sidebar.
    await page.goto(base + '/agents');
    const project = page.getByRole('region', { name: 'Project Example project', exact: true });
    await project.waitFor({ timeout: 60000 });
    assert.equal(await project.locator('.agent-row').count(), 1, 'stopped workspace hidden by default');
    const shortcut = page.locator('.app-nav .nav-agent');
    await shortcut.first().waitFor({ timeout: 60000 });
    assert.equal(await shortcut.count(), 1, 'one running and one stopped stays a single sidebar shortcut');
    assert.ok((await shortcut.locator('a').getAttribute('href'))?.endsWith(id(10)), 'shortcut targets the running agent');
    await page.getByRole('region', { name: 'Project Stopped project', exact: true }).waitFor();
    await page.getByLabel('Show stopped workspaces', { exact: true }).check();
    assert.equal(await project.locator('.agent-row').count(), 2);
    await page.screenshot({ path: '/tmp/dam-project-workspaces-desktop.png', fullPage: true });
    await page.getByLabel('Show stopped workspaces', { exact: true }).uncheck();
    agents[1].state = 'Running';
    await page.reload();
    await page.locator('.nav-project').waitFor({ timeout: 60000 });
    assert.equal(await page.locator('.nav-project .nav-agent').count(), 2, 'two running agents expand into separate links');
    agents[1].state = 'Stopped';
    // The dashboard and a Runner's page group workspaces with the same component.
    for (const path of ['/', `/runners/${id(1)}`]) {
        await page.goto(base + path);
        const group = page.getByRole('region', { name: 'Project Example project', exact: true });
        await group.waitFor({ timeout: 60000 });
        assert.equal(await group.locator('.agent-row').count(), 1, `${path}: the stopped workspace waits behind Show stopped`);
        await group.getByRole('button', { name: /Show 1 stopped workspace/ }).click();
        assert.equal(await group.locator('.agent-row').count(), 2, `${path}: both workspaces are under their project`);
        await page.getByRole('region', { name: 'Project Stopped project', exact: true }).waitFor();
    }

    // The project's own actions are in its heading, once, and act on Main even when a workspace replied last; the
    // rows leave them out. A workspace's own menu keeps what is its own.
    Object.assign(agents[1], { lastAssistantMessageAt: new Date().toISOString() });
    await page.goto(base + '/agents');
    await page.getByLabel('Show stopped workspaces', { exact: true }).check();
    await project.locator('.agent-row').nth(1).waitFor({ timeout: 60000 });
    assert.equal(await project.locator('.agent-row .list-title').first().innerText(), 'login-fix', 'the workspace that replied last comes first');
    for (const row of [0, 1]) {
        const more = project.locator('.agent-row').nth(row).locator('.agent-row-actions .mud-menu button');
        await more.click();
        await page.getByRole('menuitem', { name: 'Share...', exact: true }).waitFor();
        for (const entry of [/^New workspace/, /^Move/, /^Recover move/])
            assert.equal(await page.getByRole('menuitem', { name: entry }).count(), 0, `row ${row} leaves ${entry} to the project heading`);
        // Escape doesn't close MudBlazor menus here; ⋮ toggles it closed.
        await more.click();
        await page.getByRole('menuitem', { name: 'Share...', exact: true }).waitFor({ state: 'hidden' });
    }
    const heading = project.locator('.workspace-project-heading');
    const asked = (what: RegExp) => page.waitForRequest(request => what.test(new URL(request.url()).pathname));
    let request = asked(/\/move\/processes$/);
    await heading.getByRole('button', { name: 'Move project', exact: true }).click();
    assert.equal(new URL((await request).url()).pathname, `/api/agents/${id(10)}/move/processes`, 'Move project moves from Main');
    const move = page.getByRole('dialog');
    await move.getByText('Move Example project to another runner', { exact: true }).waitFor();
    await move.getByText('Main and its 1 workspace move together.', { exact: true }).waitFor();
    await move.getByRole('button', { name: 'Cancel', exact: true }).click();
    await move.waitFor({ state: 'hidden' });
    request = asked(/\/workspaces\/estimate$/);
    await heading.getByRole('button', { name: 'New workspace', exact: true }).click();
    assert.equal(new URL((await request).url()).pathname, `/api/agents/${id(10)}/workspaces/estimate`, 'a new workspace starts from Main');
    await page.keyboard.press('Escape');
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    // An interrupted move is recovered from the heading too.
    for (const a of agents.slice(0, 2)) Object.assign(a, { movePending: true, state: 'Error', stateMessage: 'Move failed' });
    await page.reload();
    await heading.getByRole('button', { name: 'Recover move', exact: true }).waitFor({ timeout: 60000 });
    assert.equal(await heading.getByRole('button', { name: 'Move project', exact: true }).count(), 0);
    assert.equal(writes.length, 0);
    await heading.getByRole('button', { name: 'Recover move', exact: true }).click();
    await written(1);
    assert.deepEqual(writes.map(w => w.path), [`/api/agents/${id(10)}/move/recover`]);
    writes.length = 0;
    for (const a of agents.slice(0, 2)) Object.assign(a, { movePending: false, state: 'Stopped', stateMessage: null });
    agents[0].state = 'Running';
    Object.assign(agents[1], { lastAssistantMessageAt: null });
    await page.getByLabel('Show stopped workspaces', { exact: true }).uncheck();

    // The dropdown: rows labelled by their conversation, with ▶/■; opening sends nothing.
    await page.goto(base + `/agents/${id(10)}`);
    const picker = page.locator('button[aria-label="Choose workspace"]');
    await picker.waitFor({ timeout: 60000 });
    slowWorkspace = true;
    await picker.click();
    await page.getByRole('status').filter({ hasText: 'Loading workspaces' }).waitFor();
    await page.getByRole('menuitem', { name: /Current conversation/ }).waitFor();
    slowWorkspace = false;
    await page.getByRole('menuitem', { name: /Stopped \(1\)/ }).click();
    await page.getByRole('menuitem', { name: /^login-fix/ }).waitFor();
    // Rows lead with the workspace's name; the branch and the conversation are underneath.
    await page.getByRole('menuitem', { name: /^login-fix/ }).getByText('switched-branch', { exact: true }).waitFor();
    await page.getByRole('menuitem', { name: /^Main/ }).getByText('main · Current conversation', { exact: true }).waitFor();
    // And what each one runs with: its harness, and the login for harnesses that have logins.
    const runsWith = async (row: RegExp) => (await page.getByRole('menuitem', { name: row }).locator('.workspace-menu-login').innerText()).replace(/\s+/g, ' ').trim();
    assert.equal(await runsWith(/^login-fix/), 'Claude Code · admin · Work login');
    assert.equal(await runsWith(/^Main/), 'pi', 'a harness without logins shows only its name');
    assert.deepEqual(writes, [], 'opening the dropdown sends no lifecycle commands');
    await page.getByRole('button', { name: 'Stop Main', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Start login-fix', exact: true }).click();
    await written(1);
    assert.deepEqual(writes.map(w => w.path), [`/api/agents/${id(11)}/start`], '▶ starts only that workspace');
    await page.getByRole('menuitem', { name: /Previous conversation/ }).waitFor();
    await page.getByRole('menuitem', { name: /Removed workspace chat/ }).getByText('old-fix (removed)', { exact: false }).waitFor();
    assert.equal(await page.getByRole('menuitem', { name: /Current conversation/ }).count(), 1, 'active conversations show only as workspaces');
    assert.equal(await page.getByRole('menuitem', { name: 'All workspaces and conversations', exact: true }).getAttribute('href'), `projects/${id(50)}`);

    // Choosing a saved conversation asks first.
    await page.getByRole('menuitem', { name: /Previous conversation/ }).click();
    const chooser = page.getByRole('dialog');
    await chooser.getByRole('button', { name: 'Resume here, in Main' }).waitFor();
    await chooser.getByText('“Current conversation” is saved and stops.', { exact: true }).waitFor();
    assert.equal(await chooser.locator('.conversation-chooser-warning').count(), 0, 'no warning for the workspace it last ran in');
    assert.equal(writes.length, 1, 'choosing a conversation does nothing by itself');
    await chooser.getByRole('button', { name: 'Resume here, in Main' }).click();
    await chooser.waitFor({ state: 'hidden' });
    await written(2);
    assert.deepEqual(writes.at(-1), { path: `/api/agents/${id(10)}/session`, body: { sessionId: id(101), title: null, update: false } });

    // From another workspace: a warning, its branch, and what is still only in the workspace it last ran in.
    await picker.click();
    await page.getByRole('menuitem', { name: /Login fix chat/ }).click();
    const warning = chooser.locator('.conversation-chooser-warning');
    await warning.getByText('This conversation last ran in login-fix. Here it continues with Main\'s files: changes it made there that weren\'t committed and pushed aren\'t in this folder.', { exact: true }).waitFor();
    await warning.getByText('It was on login-fix; Main is on main.', { exact: true }).waitFor();
    await warning.getByText('login-fix still has 3 uncommitted files and 2 unpushed commits.', { exact: true }).waitFor();
    await chooser.screenshot({ path: '/tmp/dam-conversation-chooser-warning.png' });
    await chooser.getByRole('button', { name: 'Cancel', exact: true }).click();
    await chooser.waitFor({ state: 'hidden' });
    assert.equal(writes.length, 2, 'checking the other workspace changes nothing');

    // New workspace: each start point fills in on its own; too large is disabled; failures retry.
    let exactArrived = false;
    slowExact = new Promise(resolve => { releaseExact = () => { exactArrived = true; resolve(); }; });
    slowAccounts = true;
    await picker.click();
    await page.getByRole('menuitem', { name: 'New workspace…', exact: true }).click();
    const create = page.getByRole('dialog');
    await create.getByRole('status').filter({ hasText: 'Loading harnesses and logins' }).waitFor();
    assert.equal(await create.getByRole('alert').count(), 0, 'nothing reads as an error while the dialog loads');
    slowAccounts = false;
    await create.getByText('38 MB · 1,204 files').first().waitFor();
    await create.getByRole('status').filter({ hasText: 'Calculating…' }).waitFor();
    await create.getByText('Size unknown · Retry', { exact: true }).waitFor();
    assert.ok(!exactArrived);
    releaseExact();
    await create.getByText('needs 20 GB, 14 GB free', { exact: true }).waitFor();
    assert.equal(await create.getByRole('radio', { name: 'Exact copy, including ignored files' }).isDisabled(), true, 'an option larger than the free space is disabled');
    await create.getByText('14 GB free · copies are normal copies (this disk has no copy-on-write)', { exact: true }).waitFor();
    failCurrent = false;
    await create.getByText('Size unknown · Retry', { exact: true }).click();
    await create.getByText('39 MB · 1,216 files', { exact: true }).waitFor();
    await create.getByLabel('Pull first').check();
    await create.getByText('about 38 MB · 1,204 files', { exact: true }).waitFor();
    // What this workspace uses comes first, marked (current), and is preselected.
    await create.getByText('Personal login (current)', { exact: true }).waitFor();
    await create.getByRole('combobox', { name: 'Harness login' }).click();
    assert.deepEqual(await page.getByRole('option').allInnerTexts(), ['Personal login (current)', "Owner's permitted default", 'Work login']);
    await page.keyboard.press('Escape');
    await create.getByLabel('Name (optional)', { exact: true }).fill('fix-toolbar');
    await create.screenshot({ path: '/tmp/dam-new-workspace.png' });
    await create.getByRole('button', { name: 'Create workspace', exact: true }).click();
    await page.waitForURL(base + `/agents/${id(12)}`);
    const created = writes.at(-1);
    assert.equal(created.path, `/api/agents/${id(10)}/workspaces`);
    assert.deepEqual([created.body.name, created.body.start, created.body.pull, created.body.startAgent, created.body.accountId], ['fix-toolbar', 'MainBranch', true, true, id(302)]);

    // Removing a workspace lists what would be lost and offers Keep files.
    await page.goto(base + `/agents/${id(11)}`);
    await page.getByLabel('More actions', { exact: true }).click();
    // A workspace's terminal can move its project; the label says it is the whole project.
    await page.getByRole('menuitem', { name: 'Move project to another runner…', exact: true }).waitFor();
    await page.getByRole('menuitem', { name: 'Remove workspace…', exact: true }).click();
    const remove = page.getByRole('dialog');
    await remove.getByText('3 files have uncommitted changes (Program.cs, app.css, notes.md)', { exact: true }).waitFor();
    await remove.getByText('2 commits aren\'t on any remote branch', { exact: true }).waitFor();
    await remove.getByText('Its conversation stays in the project\'s history.', { exact: true }).waitFor();
    const before = writes.length;
    await remove.getByRole('button', { name: 'Keep files', exact: true }).click();
    await page.waitForURL(base + `/projects/${id(50)}`);
    await written(before + 1);
    assert.deepEqual(writes.at(-1), { path: `/api/agents/${id(11)}/workspace/remove`, body: { keepFiles: true } });

    // The project page: workspaces, kept folders and conversations by where they last ran.
    await page.getByRole('region', { name: 'Kept folders', exact: true }).getByText('kept-fix', { exact: true }).waitFor();
    const conversations = page.getByRole('region', { name: 'Conversations', exact: true });
    await conversations.getByText('old-fix (removed)', { exact: true }).waitFor();
    await conversations.getByText('● Active in Main', { exact: true }).waitFor();
    // The same rows as the Sessions page, with its actions.
    assert.equal(await conversations.getByRole('button', { name: 'Rename session', exact: true }).count(), 4);
    assert.equal(await conversations.getByRole('button', { name: 'Resume in its project', exact: true }).count(), 3, 'saved conversations can resume');

    failWorkspace = true;
    await page.goto(base + `/agents/${id(10)}`);
    await picker.click();
    await page.getByRole('menuitem', { name: "Couldn't load. Retry", exact: true }).waitFor();
    failWorkspace = false;
    await page.getByRole('menuitem', { name: "Couldn't load. Retry", exact: true }).click();
    await page.getByRole('menuitem', { name: /Current conversation/ }).waitFor();
    await page.keyboard.press('Escape');
    for (const theme of ['light', 'dark']) {
        await page.evaluate(value => window.damTheme.set(value), theme);
        await page.setViewportSize({ width: 390, height: 844 });
        await page.goto(base + '/agents');
        await page.getByRole('region', { name: 'Project Example project', exact: true }).waitFor();
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme}: project groups fit phones`);
        await page.screenshot({ path: `/tmp/dam-project-workspaces-phone-${theme}.png`, fullPage: true });
        await page.goto(base + `/agents/${id(10)}`);
        await picker.click();
        await page.getByRole('menuitem', { name: /Previous conversation/ }).waitFor();
        const menu = await page.locator('.mud-popover-open').last().boundingBox();
        assert.ok(menu.x >= 0 && menu.x + menu.width <= 391, `${theme}: the dropdown fits phones`);
        await page.screenshot({ path: `/tmp/dam-workspace-menu-phone-${theme}.png` });
        await page.keyboard.press('Escape');
        await page.goto(base + `/projects/${id(50)}`);
        await page.getByRole('region', { name: 'Conversations', exact: true }).getByText('Previous conversation', { exact: true }).waitFor();
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme}: the project page fits phones`);
        await page.screenshot({ path: `/tmp/dam-project-page-phone-${theme}.png`, fullPage: true });
        await page.setViewportSize({ width: 1400, height: 1000 });
    }
    assert.deepEqual(errors, []);
    console.log('PASS: project grouping, dropdown ▶/■ and branches, conversation chooser, New workspace sizes (calculating, retry, too large, pull), Keep files removal, project page, slow/failing menus and phone themes');
} finally { await browser.close(); }
