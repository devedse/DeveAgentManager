const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

// A conversation larger than the Manager shows is described, not loaded: the page keeps its facts and explains why,
// instead of an error with Retry (docs/manager-memory.md). A conversation within the limit is still shown.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const large = '00000000-0000-0000-0000-000000000001', small = '00000000-0000-0000-0000-000000000002';
const now = new Date().toISOString();

try {
    for (const [width, height] of [[1400, 900], [390, 844]]) {
        const context = await browser.newContext({ viewport: { width, height } });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
        await page.goto(base + '/login');
        await page.locator('input[name="username"]').fill('admin');
        await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
        const me = await (await context.request.get(base + '/api/me')).json();
        const session = (id, title, savedSize) => ({ id, ownerId: me.id, ownerName: 'Admin', access: 'Owner', title, harnessId: 'claude', messageCount: 5120,
            createdAt: now, updatedAt: now, lastSyncedAt: now, savedSize, totalSize: savedSize });
        const contents = [];
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/info' || path === '/api/diagnostics/cpu') return route.continue();
            if (path === `/api/sessions/${large}`) return route.fulfill({ json: session(large, 'Huge conversation', 452 * 1048576) });
            if (path === `/api/sessions/${small}`) return route.fulfill({ json: session(small, 'Small conversation', 2048) });
            if (path.endsWith('/content')) {
                contents.push(path);
                return path.includes(large)
                    ? route.fulfill({ status: 413, contentType: 'application/problem+json', body: JSON.stringify({ status: 413, detail: 'This conversation is 452 MB, more than the 64 MB the Manager shows here.' }) })
                    : route.fulfill({ json: { format: 'deve-session', version: 1, entries: [{ id: 'm1', kind: 'message', role: 'assistant', content: [{ type: 'text', text: 'Small and shown.' }], timestamp: now }] } });
            }
            return route.fulfill({ json: [] });
        });

        await page.goto(base + `/sessions/${large}`);
        const notice = page.getByRole('status').filter({ hasText: 'Conversation not shown' });
        await notice.waitFor({ timeout: 60000 });
        assert.match(await notice.innerText(), /452 MB, more than the 64 MB the Manager shows here/);
        assert.equal(await page.locator('.loading-error').count(), 0, 'Too large is explained, not an error with Retry');
        const facts = await page.locator('.session-facts').innerText();
        assert.match(facts, /5120/);
        assert.doesNotMatch(facts, /tool calls/, 'No tool-call count from a conversation that was not loaded');
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${width}px: no horizontal scroll`);
        await page.screenshot({ path: `/tmp/dam-conversation-too-large-${width}.png` });

        await page.goto(base + `/sessions/${small}`);
        await page.getByText('Small and shown.').waitFor({ timeout: 60000 });
        assert.equal(await page.getByText('Conversation not shown').count(), 0);
        assert.deepEqual(contents, [`/api/sessions/${large}/content`, `/api/sessions/${small}/content`], 'Each conversation is fetched once');
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log(`Passed (${engine}): a conversation too large to show is described with its reason and facts, a small one is still shown, each fetched once, on desktop and phone.`);
} finally {
    await browser.close();
}
