const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

const browser = await playwright[process.env.DAM_TEST_BROWSER ?? 'chromium'].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000010', now = new Date().toISOString();
    const content = { format: 'deve-session', version: 1, sources: [], leafId: 'a2', migration: {
        version: 1, createdAt: now, from: 'Codex', to: 'Claude Code', preservedCalls: 1, archivedExchanges: 1,
        omittedExchanges: 2, omittedImages: 0, budgetCharacters: 320000, issues: [{ reason: 'incomplete-exchange', count: 1 }],
    }, entries: [
        { id: 'n', kind: 'message', role: 'user', migrationKind: 'notice', content: [{ type: 'text', text: 'Transfer information.' }] },
        { id: 'u', parentId: 'n', kind: 'message', role: 'user', content: [{ type: 'text', text: 'The actual user request.' }] },
        { id: 'a', parentId: 'u', kind: 'message', role: 'assistant', content: [{ type: 'toolCall', id: 'call', name: 'exec_command', toolKind: 'shell', arguments: { cmd: 'printf historical' } }] },
        { id: 't', parentId: 'a', kind: 'message', role: 'tool', content: [{ type: 'toolResult', toolCallId: 'call', isError: true, content: [{ type: 'text', text: 'Historical error output.' }] }] },
        { id: 'h', parentId: 't', kind: 'message', role: 'user', migrationKind: 'archive', content: [{ type: 'text', text: '[DAM historical record: incomplete-exchange]\nUnverified historical data <script>window.executed=true</script>' }] },
        { id: 'a2', parentId: 'h', kind: 'message', role: 'assistant', content: [{ type: 'text', text: 'Ready for new work.' }] },
    ] };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path.startsWith('/api/me')) return route.continue();
        const body = path === '/api/info' ? info : path === `/api/sessions/${id}`
            ? { id, access: 'Read', ownerName: 'Test user', title: 'Migrated session', harnessId: 'claude', harnessTrail: 'codex,claude', messageCount: 5, createdAt: now, updatedAt: now }
            : path === `/api/sessions/${id}/content` ? content : [];
        return route.fulfill({ json: body });
    });
    for (const width of [1280, 390]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + `/sessions/${id}`);
        await page.getByText('1 tool calls preserved', { exact: false }).waitFor();
        assert.equal(await page.locator('.conv-user').count(), 1, 'Archival context must not look like a new user request');
        const history = page.locator('details.conv-system').filter({ hasText: 'Historical context' });
        await history.locator('summary').click();
        assert.match(await history.locator('pre').textContent(), /incomplete-exchange/);
        assert.equal(await history.locator('script').count(), 0);
        assert.equal(await page.evaluate(() => window.executed), undefined);
        const tool = page.locator('details.conv-tool');
        assert.equal(await tool.count(), 1, 'One actual historical call');
        await tool.locator('summary').click();
        await tool.getByText('Historical error output.', { exact: true }).waitFor();
        assert.match(await tool.locator('summary').textContent(), /failed/);
        await page.getByRole('button', { name: 'Help about Conversation migration', exact: true }).click();
        await page.getByText('incomplete-exchange: 1', { exact: true }).waitFor();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `Overflow at ${width}px`);
    }
    assert.deepEqual(errors, []);
    console.log('Passed: migration report, distinct archival context, actual tool/error display, inert source text and phone layout.');
} finally {
    await browser.close();
}
