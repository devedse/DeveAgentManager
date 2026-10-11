const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Text selected in xterm.js gets a Copy button where the selection ended: Shift+drag where tmux has the mouse (a plain
// drag still goes to tmux), a plain drag on a read-only terminal. The button and Ctrl+C copy, say so, and keep the
// terminal focused.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch(engine === 'firefox' ? { firefoxUserPrefs: { 'dom.events.testing.asyncClipboard': true } } : {});
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...(engine === 'chromium' ? { permissions: ['clipboard-read', 'clipboard-write'] } : {}) });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date().toISOString();
    const reply = '**Reply**\n\n```cs\ncode\n```';
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path.startsWith('/api/me')) return route.continue();
        const body = path === '/api/info' ? info : path.endsWith('/content') ? {
            format: 'deve-session', version: 1, leafId: 'assistant', entries: [
                { id: 'user', kind: 'message', role: 'user', timestamp: now, content: [{ type: 'text', text: 'Hello **there**' }] },
                { id: 'assistant', parentId: 'user', kind: 'message', role: 'assistant', timestamp: now, content: [{ type: 'thinking', text: 'not copied' }, { type: 'text', text: reply }] }
            ]
        } : path === `/api/sessions/${id}` ? { id, title: 'Copy test', access: 'Read', ownerName: 'Test owner', harnessId: 'claude', messageCount: 2, updatedAt: now } : [];
        return route.fulfill({ json: body });
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(base + `/sessions/${id}`);
    const buttons = page.getByRole('button', { name: 'Copy message', exact: true });
    await buttons.first().waitFor({ timeout: 60000 });
    assert.equal(await buttons.count(), 2);
    await buttons.first().click();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), 'Hello **there**');
    await buttons.nth(1).click();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), reply);
    await page.locator('.page-header .mud-menu button').last().click();
    await page.getByText('Copy last reply', { exact: true }).click();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), reply);
    console.log(`Passed (${engine}): read-only conversation copies user messages and last assistant reply as clean Markdown.`);
} finally { await browser.close(); }
