const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// CPU, RAM and disk are read inside the Runner. A ready Runner that can't be read (e.g. SSH fails) keeps its bars and
// shows "–" for all three, on the Runners page, the dashboard and the Runner page, instead of a mix of values and gaps.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const now = new Date().toISOString();
const gib = 1073741824;

try {
    for (const [width, height] of [[1400, 1000], [390, 844]]) {
        const context = await browser.newContext({ viewport: { width, height } });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
        await page.goto(base + '/login');
        await page.locator('input[name="username"]').fill('admin');
        await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
        const info = await (await context.request.get(base + '/api/info')).json();
        const me = await (await context.request.get(base + '/api/me')).json();
        const runner = (n, name, status) => ({ id: id(n), name, ownerId: me.id, ownerName: 'Admin', access: 'Owner', providerId: 'existing', kind: 'Headless',
            state: 'Ready', host: `runner-${n}.test`, username: 'runneruser', cores: 4, memoryMb: 8192, diskGb: 64, agents: [], harnessIds: [], toolIds: [],
            createdAt: now, updatedAt: now, status });
        const readable = runner(1, 'readable', { powerState: 'Running', cpuUsage: 24.8, memoryUsedBytes: Math.round(8.2 * gib), memoryTotalBytes: Math.round(13.3 * gib), diskUsedBytes: 30 * gib, diskTotalBytes: 60 * gib });
        const unreachable = runner(2, 'unreachable', { powerState: 'Running', address: 'runner-2.test' });
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            const byId = [readable, unreachable].find(r => path === `/api/runners/${r.id}`);
            const body = path === '/api/info' ? info : path === '/api/runners' ? [readable, unreachable] : byId ? byId
                : path.endsWith('/members') ? [] : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
            return route.fulfill({ json: body });
        });
        const values = locator => locator.locator('.usage-value').allInnerTexts();

        await page.goto(base + '/runners');
        const card = name => page.locator('.runner-card', { hasText: name });
        await card('unreachable').locator('.usage').waitFor({ timeout: 60000 });
        assert.deepEqual(await values(card('unreachable')), ['–', '–', '–'], `${width}px: an unreadable Runner shows no values`);
        assert.deepEqual(await values(card('readable')), ['25%', '8.2 / 13.3 GB', '30.0 / 60 GB'], `${width}px: readings from inside the Runner`);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${width}px: no horizontal scroll`);
        await page.screenshot({ path: `/tmp/dam-runner-usage-${width}.png` });

        await page.goto(base + '/');
        const row = page.locator('.usage-compact').last();
        await row.waitFor({ timeout: 60000 });
        assert.equal(await page.locator('.usage-compact').count(), 2, 'Dashboard: both ready Runners keep their bars');
        assert.deepEqual(await values(row), ['–', '–', '–']);

        await page.goto(base + `/runners/${unreachable.id}`);
        const resources = page.locator('.panel', { hasText: 'Resources' });
        await resources.locator('.usage').waitFor({ timeout: 60000 });
        assert.deepEqual(await values(resources.locator('.usage').first()), ['–', '–', '–'], 'Runner page: the Resources panel stays, without values');
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log(`Passed (${engine}): readings from inside the Runner, and an unreadable ready Runner shows "–" for CPU, RAM and disk on the Runners page, dashboard and Runner page, on desktop and phone.`);
} finally {
    await browser.close();
}
