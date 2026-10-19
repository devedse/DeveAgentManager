const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Loading states (AGENTS.md, "UI: loading states"): with a slow API, sections show placeholders (not empty states or
// zeros) after 300 ms; with a fast API nothing flashes; a failed load shows its error with Retry; menus and dialogs too;
// later refreshes keep the data on screen.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const now = new Date().toISOString();
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
// The highest opacity any placeholder reached, sampled every frame.
// Every LoadingSection placeholder: how soon after it appeared in the page it became visible, and how long it then stayed.
const sampler = () => {
    window.placeholderPeak = 0;
    window.placeholderSeen = '';
    window.placeholderVisits = [];
    window.placeholderEarliest = Infinity;
    const tracked = new Map();
    const sample = () => {
        const now = performance.now();
        const current = new Set(document.querySelectorAll('.loading-placeholder:not(.menu-loading)'));
        for (const el of current) {
            if (!tracked.has(el)) tracked.set(el, { inserted: now, visibleAt: null, label: el.textContent.trim().slice(0, 40) });
            const entry = tracked.get(el);
            const style = getComputedStyle(el);
            const opacity = style.visibility === 'hidden' ? 0 : +style.opacity;
            if (opacity > window.placeholderPeak) { window.placeholderPeak = opacity; window.placeholderSeen = `"${entry.label}"`; }
            if (opacity > 0.05 && entry.visibleAt === null) {
                entry.visibleAt = now;
                window.placeholderEarliest = Math.min(window.placeholderEarliest, now - entry.inserted);
            }
        }
        for (const [el, entry] of tracked) {
            if (current.has(el)) continue;
            if (entry.visibleAt !== null) window.placeholderVisits.push({ label: entry.label, after: Math.round(entry.visibleAt - entry.inserted), shown: Math.round(now - entry.visibleAt) });
            tracked.delete(el);
        }
        requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
};
await context.addInitScript(sampler);
const page = await context.newPage();
const errors = [];
page.on('pageerror', error => errors.push(error.message));
await page.goto(base + '/login');
await page.locator('input[name="username"]').fill('admin');
await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
const info = await (await context.request.get(base + '/api/info')).json();
const me = await (await context.request.get(base + '/api/me')).json();
const runner = { id: id(1), name: 'runner-01', ownerId: me.id, ownerName: 'Test user', access: 'Owner', providerId: 'existing', kind: 'Headless', state: 'Ready',
    host: 'runner.test', cores: 4, memoryMb: 8192, diskGb: 64, agents: [], harnessIds: ['claude'], toolIds: [], createdAt: now, updatedAt: now };
// The navigation lists the running agents from the Runners.
const agent = n => ({ id: id(10 + n), runnerId: id(1), ownerId: me.id, ownerName: 'Test user', access: 'Owner', runnerName: 'runner-01', runnerState: 'Ready',
    runnerHarnessIds: ['claude'], runnerToolIds: [], name: `agent-${n}`, harnessId: 'claude', workingDirectory: `/work/agent-${n}`, state: 'Running',
    loginOwnerId: me.id, loginOwnerName: 'Test user', createdAt: now, updatedAt: now });

let delay = 0;                 // ms for every mocked API answer
const slow = new Set();        // paths answered after `delay`; others at once
let failing = new Set();       // paths answered with a 500
let agents = [agent(1), agent(2)];
let runners = [{ ...runner, agents: [agent(1)] }];
await context.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
    if (slow.has(path)) await new Promise(resolve => setTimeout(resolve, delay));
    if (failing.has(path)) return route.fulfill({ status: 500, json: { title: 'Test-only failure', detail: 'Test-only failure' } }).catch(() => { });
    const body = path === '/api/info' ? info : path === '/api/agents' ? agents : path === '/api/runners' ? runners
        : path === '/api/agents/attention' ? [] : path === '/api/sessions' ? [] : path === '/api/accounts' ? [] : [];
    return route.fulfill({ json: body }).catch(() => { });
});
const opacity = locator => locator.first().evaluate(el => +getComputedStyle(el).opacity);
const settle = ms => page.waitForTimeout(ms);
// Leaving a page mid-start aborts its downloads; only errors after the app is up count.
const startPage = async path => { await page.goto(base + path); await page.locator('.mud-main-content, .terminal-page').first().waitFor({ timeout: 60000 }); errors.length = 0; };

try {
    // 1. Slow dashboard: invisible at first, then skeletons; never "No agents" or "0 / 0".
    delay = 2500; ['/api/agents', '/api/runners'].forEach(p => slow.add(p));
    await startPage('/');
    const placeholder = page.locator('.loading-placeholder');
    await placeholder.first().waitFor({ state: 'attached', timeout: 60000 });
    await settle(700);
    assert.equal(await opacity(placeholder), 1, 'then the placeholder shows');
    // Measured every frame in the page itself, so a slow test browser can't blur it.
    assert.ok(await page.evaluate(() => window.placeholderEarliest) >= 250, `placeholders stay invisible for the first 300 ms (${await page.evaluate(() => window.placeholderEarliest)} ms)`);
    assert.ok(await page.locator('[role="status"]', { hasText: 'Loading agents' }).count() >= 1, 'a labelled status for screen readers');
    assert.ok(await page.locator('.stat-value .skeleton').count() === 4, 'the dashboard numbers are skeletons');
    assert.equal(await page.getByText('No agents', { exact: true }).count(), 0, 'no empty state while loading');
    assert.equal(await page.locator('.stat-value').filter({ hasText: /\d/ }).count(), 0, 'no zeros while loading');
    assert.equal(await page.locator('.stat-grid').getAttribute('aria-busy'), 'true');
    assert.equal(await opacity(page.locator('.nav-skeleton-agent')), 1, 'the navigation shows where its running agents will be');
    assert.equal(await page.locator('.app-nav').getByText('agent-1').count(), 0);
    await page.screenshot({ path: '/tmp/dam-loading-dashboard.png' });
    await page.getByText('agent-1', { exact: true }).first().waitFor({ timeout: 10000 });
    await page.locator('.app-nav').getByText('agent-1').waitFor({ timeout: 5000 });
    assert.equal(await page.locator('.nav-skeleton-agent').count(), 0);
    assert.equal(await page.locator('.stat-value').first().textContent(), '2 / 2');
    assert.equal(await page.locator('.loading-placeholder').count(), 0);

    // 2. Refreshing keeps the data: the next poll is slow, and nothing turns back into placeholders.
    await settle(5600);
    assert.equal(await page.locator('.loading-placeholder, .stat-value .skeleton').count(), 0, 'a refresh keeps the data on screen');
    assert.ok(await page.getByText('agent-1', { exact: true }).first().isVisible());

    // 3. Fast: nothing flashes. Without mocks, whose interception alone adds a few hundred milliseconds.
    slow.clear();
    const fast = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    await fast.addInitScript(sampler);
    const fastPage = await fast.newPage();
    await fastPage.goto(base + '/login');
    await fastPage.locator('input[name="username"]').fill('admin');
    await fastPage.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([fastPage.waitForURL(base + '/'), fastPage.locator('button[type="submit"]').click()]);
    for (const path of ['/', '/agents', '/runners', '/sessions']) {
        await fastPage.goto(base + path);
        await fastPage.locator('.mud-main-content').waitFor({ timeout: 60000 });
        await fastPage.waitForTimeout(1500);
        // Usually nothing shows. When a load does take longer (a slow browser), the placeholder doesn't flash: it appears
        // after the delay and stays at least the minimum time.
        for (const visit of await fastPage.evaluate(() => window.placeholderVisits)) {
            assert.ok(visit.after >= 250 && visit.shown >= 350, `${path}: ${visit.label} flashed: ${JSON.stringify(visit)}`);
        }
    }
    await fast.close();

    // 4. A failed load says so, with Retry, instead of an empty dashboard.
    failing = new Set(['/api/agents']);
    await startPage('/');
    const retry = page.getByRole('button', { name: 'Retry' }).first();
    await retry.waitFor({ timeout: 30000 });
    assert.ok(await page.getByText('Test-only failure').first().isVisible());
    assert.equal(await page.getByText('No agents', { exact: true }).count(), 0);
    assert.equal(await page.locator('.stat-value .skeleton').count(), 0, 'a failed load stops pretending to load');
    failing = new Set();
    await retry.click();
    await page.getByText('agent-1', { exact: true }).first().waitFor({ timeout: 10000 });

    // 5. Agents, Runners and Sessions pages: their own placeholders, then content or the empty state.
    delay = 1500; ['/api/agents', '/api/runners', '/api/sessions'].forEach(p => slow.add(p));
    for (const [path, shape, done] of [['/agents', '.skeleton-row', 'agent-2'], ['/runners', '.skeleton-card', 'runner-01'], ['/sessions', '.skeleton-row', 'No sessions yet']]) {
        await startPage(path);
        await page.locator(shape).first().waitFor({ timeout: 10000 });
        await settle(500);
        assert.equal(await opacity(page.locator(shape)), 1, `${path}: placeholder shaped like its content`);
        assert.equal(await page.getByText(/^No (agents|Runners?|sessions)( yet)?$/).count(), 0, `${path}: no empty state while loading`);
        await page.getByText(done, { exact: true }).first().waitFor({ timeout: 10000 });
    }

    // 6. Menus open at once and say they are loading.
    slow.clear();
    delay = 3000; slow.add('/api/agents/attention');
    await startPage('/agents');
    await page.getByText('agent-1', { exact: true }).first().waitFor({ timeout: 10000 });
    await page.locator('button[aria-label^="Agents needing attention"]').click();
    await page.locator('.menu-loading', { hasText: 'Loading notifications' }).waitFor({ timeout: 5000 });
    assert.equal(await page.getByText('Nothing needs attention').count(), 0, 'no "nothing" while loading');
    await page.getByText('Nothing needs attention').waitFor({ timeout: 10000 });
    await page.keyboard.press('Escape');

    // 7. Dialogs: the New agent dialog doesn't claim there is no Runner while it is still asking.
    slow.delete('/api/agents/attention');
    runners = [];
    delay = 2500; slow.add('/api/runners');
    await startPage('/agents');
    await page.getByText('agent-1', { exact: true }).first().waitFor({ timeout: 10000 });
    await page.getByRole('button', { name: 'New agent' }).first().click();
    await page.getByRole('dialog').locator('.skeleton-form').waitFor({ timeout: 5000 });
    assert.equal(await page.getByText('No Runner is ready').count(), 0, 'no empty state in the dialog while loading');
    await page.getByText('No Runner is ready').waitFor({ timeout: 10000 });
    await page.keyboard.press('Escape');

    // 8. The terminal page: a skeleton name and a loading message instead of a blank page.
    delay = 2000; slow.add(`/api/agents/${id(11)}`);
    await startPage(`/agents/${id(11)}`);
    await page.locator('.terminal-toolbar .skeleton').first().waitFor({ state: 'attached', timeout: 10000 });
    await page.locator('[role="status"]', { hasText: 'Loading the agent' }).waitFor({ state: 'attached', timeout: 5000 });
    assert.equal(await page.getByText('Agent not found').count(), 0);

    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): slow sections show content-shaped placeholders after 300 ms and no empty states or zeros; fast loads don't flash; refreshes keep data; failures offer Retry; menus, dialogs and the terminal page too.`);
} catch (error) {
    await page.screenshot({ path: '/tmp/dam-loading-failure.png' }).catch(() => { });
    throw error;
} finally {
    await browser.close();
}
