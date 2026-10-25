// Run against a disposable Manager, with test-only agents: in the menu, a project's workspaces sit one step further in
// than single agents, and an agent listed after a project (here one with the same name on another Runner) is clearly
// not one of its workspaces. Long Runner names don't crowd out names.
import assert from 'node:assert/strict';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const password = process.env.DAM_TEST_PASSWORD ?? 'test-only-password';
try {
    for (const width of [1400, 390]) {
        const context = await browser.newContext({ viewport: { width, height: 900 } });
        const page = await context.newPage();
        const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
        await page.goto(base + '/login');
        await page.locator('[name=username]').fill('admin');
        await page.locator('[name=password]').fill(password);
        await Promise.all([page.waitForURL(url => url.pathname !== '/login'), page.locator('button[type=submit]').click()]);
        const info = await (await context.request.get(base + '/api/info')).json();
        const me = await (await context.request.get(base + '/api/me')).json();
        const at = minutes => new Date(Date.now() - minutes * 60000).toISOString();
        let n = 0;
        const agent = (name, runnerName, minutes, extra = {}) => ({ sharedWithMe: false, id: `00000000-0000-0000-0000-0000000000${String(++n).padStart(2, '0')}`,
            runnerId: '00000000-0000-0000-0000-000000000001', ownerId: me.id, ownerName: 'Test user', access: 'Owner', runnerName, runnerState: 'Ready',
            runnerHarnessIds: ['codex'], runnerToolIds: [], name, harnessId: 'codex', workingDirectory: `/work/${name}`, state: 'Running',
            createdAt: at(9000), updatedAt: at(1), lastAssistantMessageAt: at(minutes), ...extra });
        const project = '00000000-0000-0000-0000-0000000000a1';
        const agents = [
            agent('first-single-agent', 'runner-01', 1),
            agent('example-project', 'runner-01', 2, { projectId: project, projectName: 'example-project', repositoryBranch: 'main', workspaces: 1 }),
            agent('login-fix', 'runner-01', 3, { projectId: project, projectName: 'example-project', isClone: true, repositoryBranch: 'login-fix', workspaces: 1 }),
            agent('example-project', 'runner-with-a-long-name', 4),
        ];
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            return route.fulfill({ json: path === '/api/info' ? info : path === '/api/agents' ? agents : [] });
        });
        await page.goto(base + '/agents');
        if (width < 900) await page.locator('.mud-appbar button').first().click();
        const nav = page.locator('.app-nav');
        const group = nav.locator('.nav-project');
        await group.locator('.nav-agent').first().waitFor();
        await page.waitForTimeout(500);
        const box = async locator => (await locator.boundingBox())!;

        assert.equal(await group.locator('.nav-project-name').textContent(), 'example-project');
        assert.equal(await group.locator('.nav-project-name').getAttribute('title'), 'example-project · runner-01', 'the project tells its Runner on hover');
        assert.deepEqual(await group.locator('.nav-agent .nav-agent-name').allTextContents(), ['Main', 'login-fix']);
        assert.deepEqual(await group.locator('.nav-agent .nav-agent-meta').allTextContents(), ['main', 'login-fix']);
        const outside = page.locator('.app-nav .nav-agent:not(.nav-project .nav-agent)');
        assert.deepEqual(await outside.locator('.nav-agent-name').allTextContents(), ['first-single-agent', 'example-project'],
            'the agent after the project is listed outside it');

        // Single agents and the project's folder line up; the workspaces are further in.
        const single = await box(outside.first().locator('.nav-agent-avatar'));
        const folder = await box(group.locator('> .mud-nav-link .mud-nav-link-icon'));
        const workspace = await box(group.locator('.nav-agent .nav-agent-avatar').first());
        assert.ok(Math.abs(folder.x - single.x) <= 2, `the folder lines up with single agents (${folder.x} vs ${single.x})`);
        assert.ok(workspace.x >= single.x + 16, `workspaces are further in (${workspace.x} vs ${single.x})`);

        // Every name and Runner or branch stays within its row, and a long Runner name leaves room for the name.
        for (const row of await nav.locator('.nav-agent-row').all()) {
            const link = await box(row.locator('xpath=ancestor::*[contains(@class,"mud-nav-link")][1]'));
            const meta = await box(row.locator('.nav-agent-meta'));
            assert.ok(meta.x + meta.width <= link.x + link.width + 1, `"${await row.textContent()}" fits its row at ${width}px`);
        }
        const last = outside.last();
        const name = await box(last.locator('.nav-agent-name')), meta = await box(last.locator('.nav-agent-meta'));
        assert.ok(name.width >= meta.width, `the name keeps at least as much room as the Runner (${name.width} vs ${meta.width})`);
        assert.equal(await last.locator('.nav-agent-meta').getAttribute('title'), 'runner-with-a-long-name', 'a cut Runner name shows whole on hover');
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log('Passed: in the menu a project\'s workspaces sit further in, an agent after a project is outside it, and long names fit, at 1400 and 390px.');
} finally {
    await browser.close();
}
