import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5198';
const browser = await chromium.launch();
const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const reason = 'This agent no longer has access to its configured login. Choose another harness or login to continue.';
const pending = 'Login access ended. Waiting for the Runner to confirm the previous harness has stopped.';
try {
    for (const theme of ['light', 'dark']) {
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: theme as 'light' | 'dark' });
        await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
        const page = await context.newPage();
        page.setDefaultTimeout(60000);
        const errors: string[] = [];
        page.on('pageerror', e => errors.push(e.message));
        await page.goto(base + '/login');
        await page.locator('[name=username]').fill('admin');
        await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
        const me = await (await context.request.get(base + '/api/me')).json();
        const info = await (await context.request.get(base + '/api/info')).json();
        const now = new Date().toISOString();
        const agent = { id: id(31), ownerId: me.id, ownerName: 'Test user', access: 'Owner', name: 'example-agent', harnessId: 'claude',
            runnerId: id(32), runnerName: 'test-runner', runnerState: 'Ready', runnerHarnessIds: ['codex', 'claude', 'pi'], runnerToolIds: [],
            state: 'Exited', stateMessage: 'The harness exited (code 143). Start it again to resume the conversation.',
            workingDirectory: '/workspace/example', accountId: id(33) as string | null, runningAccountId: null as string | null,
            accountName: 'Former shared login' as string | null, accountUsername: 'friend',
            loginStatus: { canStart: false, reason: reason as string | null, stopPending: false },
            createdAt: now, updatedAt: now, canUseRunner: true };
        const accounts = [
            { id: id(34), harnessId: 'claude', name: 'Personal Claude' },
            { id: id(35), harnessId: 'codex', name: 'Personal Codex' }
        ].map(a => ({ ...a, createdByUsername: 'testuser', access: 'Manage', kind: 'ApiKey', enabled: true, createdAt: now, updatedAt: now }));
        const posts: any[] = [];
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            if (route.request().method() === 'POST') {
                assert.equal(path, `/api/agents/${agent.id}/switch`, 'Recovery must use the explicit switch endpoint, never start the old login');
                const selection = route.request().postDataJSON();
                posts.push(selection);
                agent.harnessId = selection.harnessId; agent.accountId = selection.accountId; agent.runningAccountId = selection.accountId;
                agent.state = 'Running'; agent.stateMessage = null;
                agent.loginStatus = { canStart: true, reason: null, stopPending: false };
                return route.fulfill({ status: 204 });
            }
            return route.fulfill({ json: path === '/api/info' ? info : path === '/api/accounts' ? accounts : path === '/api/agents' ? [agent]
                : path === `/api/agents/${agent.id}` ? agent : path.endsWith('/session') || path.endsWith('/limit') ? null
                : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : [] });
        });
        await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            for (const scenario of ['revoked', 'deleted', 'pending']) {
                agent.state = 'Exited'; agent.harnessId = 'claude'; agent.stateMessage = 'The harness exited (code 143). Start it again to resume the conversation.';
                agent.accountId = scenario === 'deleted' ? null : id(33); agent.accountName = scenario === 'deleted' ? null : 'Former shared login';
                agent.loginStatus = { canStart: false, reason: scenario === 'pending' ? pending : reason, stopPending: scenario === 'pending' };
                await page.goto(base + `/agents/${agent.id}`);
                const stopped = page.locator('.terminal-stopped');
                await stopped.getByText(agent.loginStatus.reason, { exact: true }).waitFor();
                assert.ok(await stopped.getByRole('button', { name: 'Start agent', exact: true }).isDisabled());
                assert.equal(await stopped.getByText(/code 143/).count(), 0);
                const before = posts.length;
                const recovery = stopped.locator('button.switch-chip');
                assert.ok(await recovery.isEnabled());
                assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Stopped recovery fits phones');
                if (scenario === 'deleted' && width === 390)
                    await page.screenshot({ path: `/tmp/dam-login-recovery-${theme}.png`, fullPage: true });
                await recovery.click();
                const menu = page.locator('.switch-menu:visible');
                await menu.getByRole('button', { name: /Personal Codex/ }).waitFor();
                assert.equal(await menu.getByText('Former shared login').count(), 0);
                await menu.getByRole('button', { name: /Personal Codex/ }).click();
                await page.getByRole('dialog').getByRole('button', { name: 'Continue', exact: true }).click();
                await stopped.waitFor({ state: 'detached' });
                assert.equal(posts.length, before + 1);
                assert.deepEqual(posts.at(-1), { harnessId: 'codex', accountId: id(35) });
            }
        }
        agent.state = 'Stopped'; agent.loginStatus = { canStart: false, reason, stopPending: false };
        await page.goto(base + '/agents');
        const row = page.locator('.agent-row').filter({ hasText: agent.name });
        await row.waitFor();
        assert.equal(await row.locator('button[disabled] svg').count(), 1, 'Agent list also disables Start');
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log('Passed: revoked/deleted/pending login recovery, disabled Start, explicit cross-harness switch, both themes and phone layouts.');
} finally { await browser.close(); }
