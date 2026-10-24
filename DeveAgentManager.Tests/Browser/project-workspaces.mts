import assert from 'node:assert/strict';
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5109';
const browser = await playwright[process.env.DAM_TEST_BROWSER ?? 'chromium'].launch();
try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const me = await (await context.request.get(base + '/api/me')).json();
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
    const now = new Date().toISOString();
    const agent = (n, name, state, project = 50) => ({ id: id(n), runnerId: id(1), ownerId: me.id, ownerName: 'Test user', access: 'Owner',
        runnerName: 'test-runner', runnerState: 'Ready', runnerHarnessIds: ['pi'], runnerToolIds: [], name, harnessId: 'pi', workingDirectory: `/work/${name}`,
        state, loginOwnerId: me.id, loginOwnerName: 'Test user', createdAt: now, updatedAt: now, projectId: id(project), projectName: project === 50 ? 'Example project' : 'Stopped project',
        isWorktree: n !== 10 && n !== 20, repositoryBranch: n === 10 || n === 20 ? 'main' : name, canUseRunner: true });
    const agents = [agent(10, 'example-project', 'Running'), agent(11, 'login-fix', 'Stopped'), agent(20, 'stopped-project', 'Stopped', 60), agent(21, 'old-task', 'Stopped', 60)];
    const sessions = [0, 1].map(n => ({ id: id(100 + n), ownerId: me.id, ownerName: 'Test user', access: 'Owner', title: n ? 'Previous conversation' : 'Current conversation', harnessId: 'pi', messageCount: 2,
        agentId: n ? null : id(10), workspaceAgentId: id(10), workspaceAccess: 'Owner', createdAt: now, updatedAt: now, sourceSize: 0, savedSize: 0, totalSize: 0 }));
    const writes: string[] = [];
    let slowWorkspace = false, failWorkspace = false;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (!['GET', 'HEAD'].includes(route.request().method())) { writes.push(path); return route.fulfill({ status: 204 }); }
        if (path === '/api/agents' && slowWorkspace) await new Promise(resolve => setTimeout(resolve, 900));
        if (path === '/api/agents' && failWorkspace) return route.fulfill({ status: 503, json: { error: 'Test-only workspace failure' } });
        const selected = agents.find(a => path === `/api/agents/${a.id}`);
        const body = path === '/api/info' ? info : path === '/api/agents' ? agents : selected ? selected : path === '/api/runners' ? []
            : path.endsWith('/session') ? sessions[0] : path === '/api/sessions' ? sessions : [];
        return route.fulfill({ json: body });
    });
    await page.goto(base + '/agents');
    const project = page.getByRole('region', { name: 'Project Example project', exact: true });
    await project.waitFor({ timeout: 60000 });
    assert.equal(await project.locator('.agent-row').count(), 1, 'stopped child hidden by default');
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
    agents[0].state = 'Stopped'; agents[1].state = 'Stopped';
    await page.goto(base + `/agents/${id(10)}`);
    await page.locator('button[aria-label="Choose workspace"]').waitFor({ timeout: 60000 });
    slowWorkspace = true;
    await page.locator('button[aria-label="Choose workspace"]').click();
    await page.getByRole('status').filter({ hasText: 'Loading workspaces' }).waitFor();
    await page.getByRole('menuitem', { name: /login-fix/ }).waitFor();
    assert.deepEqual(writes, [], 'opening a stopped workspace menu sends no lifecycle commands');
    await page.keyboard.press('Escape');
    await page.locator('button[aria-label="Conversations"]').click();
    await page.getByRole('menuitem', { name: 'Previous conversation', exact: true }).waitFor();
    assert.ok(await page.getByRole('button', { name: 'Resume Previous conversation', exact: true }).isVisible());
    assert.deepEqual(writes, [], 'listing conversations does not resume them');
    await page.keyboard.press('Escape');
    failWorkspace = true;
    await page.locator('button[aria-label="Choose workspace"]').click();
    await page.getByRole('menuitem', { name: "Couldn't load. Retry", exact: true }).waitFor();
    failWorkspace = false; slowWorkspace = false;
    await page.getByRole('menuitem', { name: "Couldn't load. Retry", exact: true }).click();
    await page.getByRole('menuitem', { name: /login-fix/ }).waitFor();
    await page.keyboard.press('Escape');
    await page.goto(base + '/agents');
    for (const theme of ['light', 'dark']) {
        await page.evaluate(value => window.damTheme.set(value), theme);
        await page.setViewportSize({ width: 390, height: 844 });
        await page.getByRole('region', { name: 'Project Example project', exact: true }).waitFor();
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme}: project groups fit phones`);
        await page.screenshot({ path: `/tmp/dam-project-workspaces-phone-${theme}.png`, fullPage: true });
    }
    console.log('PASS: project grouping, stopped filtering, explicit workspace links, conversation dropdown, slow/failing menus and phone themes');
} finally { await browser.close(); }
