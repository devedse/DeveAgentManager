const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
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
    const me = await (await context.request.get(base + '/api/me')).json();
    const runnerId = '00000000-0000-0000-0000-000000000001';
    const at = minutesAgo => new Date(Date.now() - minutesAgo * 60000).toISOString();
    const agent = (n, name, lastReply, createdMinutesAgo) => ({ sharedWithMe: false, id: `00000000-0000-0000-0000-00000000001${n}`, runnerId, ownerId: me.id, ownerName: 'Test user', access: 'Owner',
        runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'], runnerToolIds: [], name, harnessId: 'claude', workingDirectory: `/work/${name}`,
        state: 'Running', runningAccountName: 'Active subscription', runningAccountUsername: 'alice', accountName: 'Next selection', accountUsername: 'bob',
        loginOwnerId: me.id, loginOwnerName: 'Test user', createdAt: at(createdMinutesAgo), updatedAt: at(1), lastAssistantMessageAt: lastReply });
    // Listed in the API in another order on purpose.
    const agents = [
        agent(1, 'an-hour-ago', at(60), 5000),
        agent(2, 'never-replied', null, 100),
        agent(3, 'just-now', at(0), 9000),
        agent(4, 'ten-minutes-ago', at(10), 20),
    ];
    const expected = ['just-now', 'ten-minutes-ago', 'an-hour-ago', 'never-replied'];
    const runner = { id: runnerId, name: 'runner-01', ownerId: me.id, ownerName: 'Test user', access: 'Owner', providerId: 'existing', kind: 'Headless', state: 'Ready',
        host: 'runner.test', cores: 4, memoryMb: 8192, diskGb: 64, agents, harnessIds: ['claude'], toolIds: [], createdAt: at(10000), updatedAt: at(1) };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
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
    for (const path of ['/agents', '/', `/runners/${runnerId}`]) {
        await page.goto(base + path);
        const accountLabels = page.locator('.agent-table .agent-account');
        await accountLabels.first().waitFor({ timeout: 60000 });
        assert.equal(await accountLabels.count(), 4);
        assert.ok((await accountLabels.allInnerTexts()).every(text => text.trim() === 'Account: alice · Active subscription'), `${path}: running login owner and subscription labels`);
    }
    // Admin access is Owner for everyone: grouping must use identity, not permission.
    agents[0].ownerId = "00000000-0000-0000-0000-000000000099";
    agents[2].ownerId = agents[0].ownerId;
    agents[2].sharedWithMe = true;
    agents[1].ownerId = me.id;
    agents[3].ownerId = me.id;
    await page.goto(base + '/agents');
    const own = page.getByRole('region', { name: 'Your agents', exact: true });
    const others = page.getByRole('region', { name: 'Other users’ agents', exact: true });
    await own.locator('.list-title').first().waitFor({ timeout: 60000 });
    assert.deepEqual((await own.locator('.list-title').allInnerTexts()).map(text => expected.find(name => text.includes(name))), ['just-now', 'ten-minutes-ago', 'never-replied']);
    assert.deepEqual((await others.locator('.list-title').allInnerTexts()).map(text => expected.find(name => text.includes(name))), ['an-hour-ago']);
    assert.equal(await page.getByRole('region', { name: 'Shared with you', exact: true }).count(), 0, 'shared agents are in the normal list');
    assert.equal(await others.locator('.account-badge').count(), 1);
    assert.equal(await own.locator('.agent-row').filter({ hasText: 'just-now' }).locator('.account-badge').count(), 1, 'shared owner remains visible to an admin');
    assert.deepEqual(await names('.app-nav .nav-agent-name'), ['just-now', 'ten-minutes-ago', 'never-replied'], 'sidebar excludes agents visible only through administration');
    for (const path of ['/', `/runners/${runnerId}`]) {
        await page.goto(base + path);
        await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.list-title').first().waitFor({ timeout: 60000 });
        assert.deepEqual((await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.list-title').allInnerTexts()).map(text => expected.find(name => text.includes(name))), ['just-now', 'ten-minutes-ago', 'never-replied'], `${path}: own order`);
        assert.equal(await page.getByRole('region', { name: 'Shared with you', exact: true }).count(), 0);
        assert.equal(await page.getByRole('region', { name: 'Other users’ agents', exact: true }).locator('.agent-row').count(), 1);
    }
    for (const path of ['/agents', '/', `/runners/${runnerId}`]) {
        await page.goto(base + path);
        await others.locator('.list-title').first().waitFor({ timeout: 60000 });
        // The runner's own list is inside an asynchronously rendered tab; the admin panel can appear first.
        await own.locator('.list-title').first().waitFor({ timeout: 60000 });
        assert.ok(await page.evaluate(() => {
            const primary = document.querySelector('section[aria-label="Your agents"]');
            const admin = document.querySelector('section[aria-label="Other users’ agents"]');
            return admin.classList.contains('panel') && primary.closest('.panel') !== admin.closest('.panel')
                && !primary.closest('.panel').contains(admin);
        }), `${path}: administrative agents have a separate panel`);
        await page.setViewportSize({ width: 390, height: 844 });
        await page.waitForTimeout(500);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${path}: both panels fit phones`);
        await page.setViewportSize({ width: 1400, height: 1000 });
    }
    const originalAgents = [...agents];
    for (let n = 0; n < 9; n++) agents.push({ ...agents[0], id: `00000000-0000-0000-0000-0000000002${n}0`, name: `recent-other-${n}`, lastAssistantMessageAt: at(0) });
    await page.goto(base + '/');
    await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.list-title').first().waitFor({ timeout: 60000 });
    assert.equal(await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.agent-row').count(), 3, 'dashboard cap cannot hide own agents behind administrative agents');
    assert.equal(await page.getByRole('region', { name: 'Other users’ agents', exact: true }).locator('.agent-row').count(), 8);
    agents.splice(0, agents.length, ...originalAgents);
    await page.goto(base + '/agents');
    await own.locator('.list-title').first().waitFor({ timeout: 60000 });
    await page.getByPlaceholder('Search agents').fill('just-now');
    await own.locator('.list-title').filter({ hasText: 'just-now' }).waitFor();
    await others.waitFor({ state: 'detached' });
    assert.equal(await own.locator('.agent-row').count(), 1);
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
    // Preferences live under the bell, are independent, and survive reload for this user/browser.
    await page.locator('button[aria-label="Agents needing attention: 0"]').click();
    const finishedSound = page.getByLabel('Finished', { exact: true });
    const limitSound = page.getByLabel('Usage limit', { exact: true });
    await finishedSound.waitFor();
    assert.equal(await finishedSound.isChecked(), false);
    await finishedSound.check();
    await limitSound.check();
    await page.waitForFunction(() => Object.keys(localStorage).some(key => key.startsWith('dam.notification-sounds.') && !key.endsWith('.played') && JSON.parse(localStorage.getItem(key)).limit === true));
    const inset = await page.locator('.notification-sounds').evaluate(el => {
        const style = getComputedStyle(el);
        return { left: parseFloat(style.paddingLeft), right: parseFloat(style.paddingRight) };
    });
    assert.ok(inset.left >= 16 && inset.right >= 16, 'sound heading and switches have menu padding');
    assert.ok(await page.locator('.notification-sounds').evaluate(el => el.scrollWidth <= el.clientWidth), 'sound controls fit the menu on phones');
    await page.locator('.notification-sounds input:disabled').waitFor({ state: 'detached' });
    await page.locator('.agent-attention-popover').screenshot({ path: '/tmp/dam-sound-switches.png' });
    await page.evaluate(() => window.damTheme.set('dark'));
    await page.waitForTimeout(300); // Allow MudBlazor's theme change to render.
    assert.ok(await page.locator('.notification-sounds').evaluate(el => el.scrollWidth <= el.clientWidth), 'dark sound controls fit the menu');
    await page.locator('.agent-attention-popover').screenshot({ path: '/tmp/dam-sound-switches-dark.png' });
    await page.reload();
    await page.locator('button[aria-label="Agents needing attention: 0"]').click();
    await finishedSound.waitFor();
    assert.equal(await finishedSound.isChecked(), true);
    assert.equal(await limitSound.isChecked(), true);
    await limitSound.uncheck();
    assert.equal(await finishedSound.isChecked(), true);
    assert.ok(await page.evaluate(async () => {
        const audio = new AudioContext();
        try {
            for (const name of ['finished', 'usage-limit']) {
                const response = await fetch(`audio/${name}.wav`);
                const buffer = await audio.decodeAudioData(await response.arrayBuffer());
                if (buffer.duration <= 0 || buffer.duration > 3) return false;
            }
            return true;
        } finally { await audio.close(); }
    }), 'both bundled sounds decode and are short');
    await context.route('**/api/agents/attention', route => route.fulfill({ status: 503, json: { error: 'Test-only notification failure' } }));
    await page.reload();
    await page.locator('button[aria-label="Agents needing attention: 0"]').click();
    await page.getByRole('menuitem', { name: "Couldn't load. Retry", exact: true }).waitFor();
    await finishedSound.uncheck();
    assert.equal(await finishedSound.isChecked(), false);
    await context.route('**/api/me', route => route.fulfill({ json: { ...me, roles: 'None' } }));
    await page.goto(base + '/agents');
    await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.list-title').first().waitFor({ timeout: 60000 });
    assert.equal(await page.getByRole('region', { name: 'Other users’ agents', exact: true }).count(), 0, 'ordinary users have no administrative section');
    assert.equal(await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.agent-row').count(), 3);
    assert.equal(await page.locator('.agent-table .agent-row').count(), 3, 'administrative-only agents are excluded from the page');
    for (const path of ['/', `/runners/${runnerId}`]) {
        await page.goto(base + path);
        await page.getByRole('region', { name: 'Your agents', exact: true }).locator('.list-title').first().waitFor({ timeout: 60000 });
        assert.equal(await page.getByRole('region', { name: 'Other users’ agents', exact: true }).count(), 0, `${path}: no ordinary-user administrative section`);
        assert.equal(await page.locator('.agent-table .agent-row').count(), 3);
    }
    console.log('Passed: independent saved sound preferences, bundled audio decoding and notification API failure.');
    console.log('Passed: ownership sections use owner identity despite admin permissions, with search and phone layouts.');
    console.log(`Passed (${engine}): agents are listed by last reply on the Agents page, dashboard, Agent activity, a Runner's page and in the navigation.`);
} finally {
    await browser.close();
}
