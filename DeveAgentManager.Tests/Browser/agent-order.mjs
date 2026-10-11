const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Everywhere agents are listed, the one that replied last comes first (AgentOrder): the Agents page, the dashboard,
// Agent activity, a Runner's agents and the navigation's running agents.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const runnerId = '00000000-0000-0000-0000-000000000001';
    const at = minutesAgo => new Date(Date.now() - minutesAgo * 60000).toISOString();
    const agent = (n, name, lastReply, createdMinutesAgo) => ({ id: `00000000-0000-0000-0000-00000000001${n}`, runnerId, ownerId: runnerId, ownerName: 'Test user', access: 'Owner',
        runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'], runnerToolIds: [], name, harnessId: 'claude', workingDirectory: `/work/${name}`,
        state: 'Running', loginOwnerId: runnerId, loginOwnerName: 'Test user', createdAt: at(createdMinutesAgo), updatedAt: at(1), lastAssistantMessageAt: lastReply });
    // Listed in the API in another order on purpose.
    const agents = [
        agent(1, 'an-hour-ago', at(60), 5000),
        agent(2, 'never-replied', null, 100),
        agent(3, 'just-now', at(0), 9000),
        agent(4, 'ten-minutes-ago', at(10), 20),
    ];
    const expected = ['just-now', 'ten-minutes-ago', 'an-hour-ago', 'never-replied'];
    const runner = { id: runnerId, name: 'runner-01', ownerId: runnerId, ownerName: 'Test user', access: 'Owner', providerId: 'existing', kind: 'Headless', state: 'Ready',
        host: 'runner.test', cores: 4, memoryMb: 8192, diskGb: 64, agents, harnessIds: ['claude'], toolIds: [], createdAt: at(10000), updatedAt: at(1) };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        const body = path === '/api/info' ? info : path === '/api/agents' ? agents : path === '/api/runners' ? [runner] : path === `/api/runners/${runnerId}` ? runner
            : path.endsWith('/members') ? [] : path.endsWith('/activity') ? [] : path.endsWith('/usage') ? [] : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
        return route.fulfill({ json: body });
    });
    const names = async selector => {
        await page.locator(selector).first().waitFor({ timeout: 60000 });
        return (await page.locator(selector).allInnerTexts()).map(text => expected.find(name => text.includes(name)));
    };

    await page.goto(base + '/agents');
    assert.deepEqual(await names('.agent-table .agent-row .list-title'), expected, 'Agents page');
    assert.deepEqual(await names('.app-nav .nav-agent-name'), expected, 'navigation');
    await page.goto(base + '/');
    assert.deepEqual(await names('.agent-table .agent-row .list-title'), expected, 'dashboard');
    await page.goto(base + '/activity');
    assert.deepEqual(await names('.activity-card-name h6'), expected, 'Agent activity');
    await page.goto(base + `/runners/${runnerId}`);
    assert.deepEqual(await names('.agent-table .agent-row .list-title'), expected, "the Runner's agents");
    console.log(`Passed (${engine}): agents are listed by last reply on the Agents page, dashboard, Agent activity, a Runner's page and in the navigation.`);
} finally {
    await browser.close();
}
