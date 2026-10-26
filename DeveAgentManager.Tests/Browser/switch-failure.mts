// Failed switches remain visible, identify the attempted destination, and never retry an ambiguous send.
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:19081';
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const browser = await chromium.launch({ headless: true });
const now = new Date().toISOString();
try {
    for (const theme of ['light', 'dark']) {
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: theme });
        await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
        const page = await context.newPage();
        const errors = []; page.on('pageerror', e => errors.push(e.message));
        await page.goto(base + '/login');
        await page.locator('[name=username]').fill('admin');
        await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
        const me = await (await context.request.get(base + '/api/me')).json();
        const info = await (await context.request.get(base + '/api/info')).json();
        const agent = { id: id(21), ownerId: me.id, ownerName: 'Admin', access: 'Owner', name: 'test-agent', harnessId: 'codex',
            runnerId: id(22), runnerName: 'test-runner', runnerState: 'Ready', runnerHarnessIds: ['codex', 'claude'], runnerToolIds: [],
            state: 'Running', workingDirectory: '/workspace/example', accountId: id(23), runningAccountId: id(23),
            accountName: 'Personal', accountUsername: 'testuser', runningAccountName: 'Personal', runningAccountUsername: 'testuser',
            createdAt: now, updatedAt: now, canUseRunner: true };
        const accounts = [
            { id: id(23), harnessId: 'codex', name: 'Personal' },
            { id: id(24), harnessId: 'claude', name: 'Work' }
        ].map(a => ({ ...a, createdByUsername: 'testuser', access: 'Manage', kind: 'ApiKey', enabled: true, createdAt: now, updatedAt: now }));
        let mode = 'verification', posts = [];
        const failure = 'Verification found different output content in historical tool result 2 (exec). The converted history was not installed. Your original conversation is retained.';
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            if (route.request().method() === 'POST') {
                posts.push({ path, body: route.request().postDataJSON() });
                assert.equal(path, `/api/agents/${agent.id}/switch`, 'Recovery must never start or restart a harness implicitly');
                if (mode === 'lost') {
                    agent.harnessId = 'claude'; agent.accountId = id(24); agent.state = 'Running';
                    return route.abort('connectionreset');
                }
                if (mode === 'success') { agent.harnessId = 'claude'; agent.accountId = id(24); agent.state = 'Running'; return route.fulfill({ status: 204 }); }
                agent.state = 'Error';
                return route.fulfill({ status: 400, json: { detail: failure, code: 'session_migration_verification' } });
            }
            const body = path === '/api/info' ? info : path === '/api/accounts' ? accounts : path === '/api/agents' ? [agent]
                : path === `/api/agents/${agent.id}` ? agent : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 }
                : path.endsWith('/limit') || path.endsWith('/session') ? null : [];
            return route.fulfill({ json: body });
        });
        await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
        const attempt = async () => {
            await page.locator('.switch-chip').click();
            await page.locator('.switch-menu:visible').getByRole('button', { name: /testuser · Work/ }).click();
            await page.getByRole('dialog').getByRole('button', { name: 'Continue', exact: true }).click();
            await page.getByRole('dialog').getByText('What went wrong', { exact: true }).waitFor();
        };
        const reset = async () => {
            agent.harnessId = 'codex'; agent.accountId = id(23); agent.state = 'Running';
            await page.goto(base + `/agents/${agent.id}`);
            await page.locator('.switch-chip').waitFor({ timeout: 60000 });
        };
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await reset(); mode = 'verification';
            await attempt();
            const dialog = page.getByRole('dialog');
            await dialog.getByText(failure, { exact: true }).waitFor();
            await dialog.getByText(/Claude Code \(testuser · Work\)/).waitFor();
            await dialog.getByText(/Codex \(testuser · Personal\)/).waitFor();
            await page.keyboard.press('Escape');
            await page.mouse.click(1, 1);
            await dialog.waitFor();
            if (width === 390) await page.waitForTimeout(6500); // Longer than the old transient snackbar.
            assert.ok(await dialog.isVisible(), 'Failure stays open until an explicit action');
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Failure dialog fits a phone');
            await page.screenshot({ path: `/tmp/dam-switch-failure-${theme}-${width}.png`, fullPage: true });
            const beforeCancel = posts.length;
            await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
            assert.equal(posts.length, beforeCancel, 'Cancel never starts or switches the harness');
            await reset(); await attempt();
            const beforeRepeatedFailure = posts.length;
            await Promise.all([
                page.waitForResponse(r => new URL(r.url()).pathname === `/api/agents/${agent.id}/switch`),
                dialog.getByRole('button', { name: 'Retry', exact: true }).click()
            ]);
            await dialog.locator('button:not(:disabled)', { hasText: /^Retry$/ }).waitFor();
            await dialog.getByText(failure, { exact: true }).waitFor();
            assert.equal(posts.length, beforeRepeatedFailure + 1, 'A repeated failure remains in the same dialog');
            mode = 'success';
            const beforeRetry = posts.length;
            await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
            await dialog.waitFor({ state: 'hidden' });
            assert.equal(posts.length, beforeRetry + 1);
            assert.deepEqual(posts.at(-1).body, { harnessId: 'claude', accountId: id(24) });
            // A shared agent changed by someone else must be reviewed before another switch.
            await reset(); mode = 'verification'; await attempt();
            agent.harnessId = 'claude'; agent.accountId = id(24); agent.state = 'Running';
            const beforeChangedAgent = posts.length;
            await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
            await dialog.getByText(/The agent changed since this attempt/).waitFor();
            assert.equal(posts.length, beforeChangedAgent);
            assert.equal(await dialog.getByRole('button', { name: 'Retry', exact: true }).count(), 0);
            await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
            // A response lost after the server switched has no safe automatic retry.
            await reset(); mode = 'lost';
            await attempt();
            assert.equal(await dialog.getByRole('button', { name: 'Retry', exact: true }).count(), 0);
            const beforeCheck = posts.length;
            await dialog.getByRole('button', { name: 'Check status', exact: true }).click();
            await dialog.getByText('Current state: Claude Code · Running.', { exact: true }).waitFor();
            assert.equal(posts.length, beforeCheck, 'An uncertain send is observed rather than repeated');
            await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        }
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log('Passed: persistent switch failures, clear source/destination and reason, explicit retry/cancel, uncertain-send status checks, both themes and phone layouts.');
} finally { await browser.close(); }
