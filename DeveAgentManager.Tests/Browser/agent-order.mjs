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
    // Admin access is Owner for everyone: grouping must use identity, not permission.
    const me = await (await context.request.get(base + '/api/me')).json();
    agents[0].ownerId = "00000000-0000-0000-0000-000000000099";
    agents[2].ownerId = agents[0].ownerId;
    agents[1].ownerId = me.id;
    agents[3].ownerId = me.id;
    await page.goto(base + '/agents');
    const own = page.getByRole('region', { name: 'Your agents', exact: true });
    const others = page.getByRole('region', { name: 'Other users’ agents', exact: true });
    await own.locator('.list-title').first().waitFor({ timeout: 60000 });
    assert.deepEqual((await own.locator('.list-title').allInnerTexts()).map(text => expected.find(name => text.includes(name))), ['ten-minutes-ago', 'never-replied']);
    assert.deepEqual((await others.locator('.list-title').allInnerTexts()).map(text => expected.find(name => text.includes(name))), ['just-now', 'an-hour-ago']);
    assert.equal(await others.locator('.account-badge').count(), 2);
    await page.getByPlaceholder('Search agents').fill('just-now');
    await own.getByText('No agents of yours match.').waitFor();
    assert.equal(await others.locator('.agent-row').count(), 1);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(500); // Allow the navigation drawer to settle after resizing.
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'ownership sections fit phones');

    const sessions = agents.slice(0, 2).map((a, i) => ({
        id: a.id, ownerId: a.ownerId, ownerName: i ? me.displayName : 'Another user', access: 'Owner',
        title: i ? 'My saved conversation' : 'Another saved conversation', harnessId: 'claude',
        messageCount: 2, createdAt: at(100), updatedAt: at(i), workspaceAccess: 'None',
        sourceSize: 0, savedSize: 0, totalSize: 0
    }));
    await context.route('**/api/sessions', route => route.fulfill({ json: sessions }));
    await page.goto(base + '/sessions');
    const mySessions = page.getByRole('region', { name: 'Your sessions', exact: true });
    const otherSessions = page.getByRole('region', { name: 'Other users’ sessions', exact: true });
    await mySessions.getByText('My saved conversation', { exact: true }).waitFor({ timeout: 60000 });
    await otherSessions.getByText('Another saved conversation', { exact: true }).waitFor();
    await otherSessions.getByText('Another user', { exact: true }).waitFor();
    assert.equal(await mySessions.locator('.agent-row').count(), 1);
    assert.equal(await otherSessions.locator('.agent-row').count(), 1);
    await page.getByPlaceholder('Search sessions').fill('My saved');
    await otherSessions.waitFor({ state: 'detached' });
    console.log('Passed: ownership sections use owner identity despite admin permissions, with search and phone layouts.');
    console.log(`Passed (${engine}): agents are listed by last reply on the Agents page, dashboard, Agent activity, a Runner's page and in the navigation.`);
} finally {
    await browser.close();
}
