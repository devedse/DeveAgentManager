const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

// A Runner's access panel lists, for those who manage it, the people without access to it whom its agents are shared
// with: read-write first (they can have commands run on it), with every agent and how, and a hover help on the risks.
// People who only use the Runner don't see other people's shares.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const runnerId = '00000000-0000-0000-0000-000000000001';
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const now = new Date().toISOString();
const agentAccess = [
    { userId: id(11), username: 'ketty', displayName: 'Ketty', disabled: false, agents: [
        { agentId: id(21), agentName: 'example-agent', ownerName: 'Alice', access: 'Write' },
        { agentId: id(22), agentName: 'docs-agent', ownerName: 'Bob', access: 'Read' }] },
    { userId: id(12), username: 'reader', displayName: 'reader', disabled: true, agents: [
        { agentId: id(22), agentName: 'docs-agent', ownerName: 'Bob', access: 'Read' }] },
];

async function run(width, height, access) {
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
    const runner = { id: runnerId, name: 'runner-01', ownerId: id(31), ownerName: 'Alice', access, providerId: 'existing', kind: 'Headless', state: 'Ready',
        host: 'runner.test', username: 'runneruser', cores: 4, memoryMb: 8192, diskGb: 64, agents: [], harnessIds: ['claude'], toolIds: [], createdAt: now, updatedAt: now };
    let requested = 0;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (path.endsWith('/agent-access') || path.endsWith('/members')) {
            if (path.endsWith('/agent-access')) requested++;
            // Slow, like a phone: both lists show they are loading, not an empty list or an error.
            await new Promise(resolve => setTimeout(resolve, 1500));
            return route.fulfill({ json: path.endsWith('/members') ? [] : agentAccess });
        }
        const body = path === '/api/info' ? info : path === '/api/runners' ? [runner] : path === `/api/runners/${runnerId}` ? runner
            : path.endsWith('/members') ? [] : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
        return route.fulfill({ json: body });
    });

    await page.goto(base + `/runners/${runnerId}`);
    const people = page.locator('.access-list').first();
    await people.waitFor({ timeout: 60000 });
    await people.locator('[role="status"][aria-busy="true"]').waitFor({ state: 'attached', timeout: 1000 });
    assert.equal(await people.locator('.loading-error').count(), 0, 'People with access: loading is not an error');
    const section = page.locator('.access-through-agents');
    if (access === 'Use') {
        await page.waitForTimeout(500);
        assert.equal(await section.count(), 0, 'Use access: no list of other people\'s shares');
        assert.equal(requested, 0, 'Use access: the list is not requested');
        await context.close();
        return;
    }

    await section.waitFor();
    // While it loads: its placeholder (shown after 300 ms), never an empty state.
    await section.locator('[role="status"][aria-busy="true"]').waitFor({ state: 'attached', timeout: 1000 });
    assert.equal(await section.getByText('No agents here').count(), 0, 'No empty state before the data');
    const rows = section.locator('.access-person');
    await rows.first().waitFor({ timeout: 10000 });
    assert.equal(await rows.count(), 2);
    const ketty = await rows.nth(0).innerText();
    assert.match(ketty, /Ketty \(ketty\)/);
    assert.match(ketty, /Read-write, can run commands:\s*example-agent \(Alice's\)/);
    assert.match(ketty, /Read-only, can watch:\s*docs-agent \(Bob's\)/);
    assert.match(await rows.nth(1).innerText(), /reader · disabled[\s\S]*Read-only, can watch:\s*docs-agent \(Bob's\)/);

    // The risks are a hover away, like every other help.
    await section.locator('.help-tip-button').hover();
    const tip = page.locator('.help-tip-content', { hasText: 'Treat read-write as shell access' });
    await tip.waitFor();
    assert.match(await tip.innerText(), /Anything printed there, such as a token or a password, is visible to them/);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${width}px: no horizontal scroll`);
    await page.screenshot({ path: `/tmp/dam-runner-agent-access-${width}.png`, fullPage: true });
    assert.deepEqual(errors, []);
    await context.close();
}

try {
    await run(1400, 1000, 'Owner');
    await run(390, 844, 'Manage');
    await run(1400, 1000, 'Use');
    console.log(`Passed (${engine}): access through agents for Runner managers, loading placeholders (no error while people load), read-write first with each agent, hover help on the risks, phone layout, and hidden for Use access.`);
} finally {
    await browser.close();
}
