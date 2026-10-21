const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Whose git identity an agent commits with: shown in the agent list and the terminal, and switched only from the Git
// identity dialog (to yourself, back to the owner, or anyone for a conversation manager). A read-write share without
// access to the Runner gets no VS Code or Runner desktop.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const agentId = '00000000-0000-0000-0000-000000000001', stoppedId = '00000000-0000-0000-0000-000000000005';
const runnerId = '00000000-0000-0000-0000-000000000002', kettyId = '00000000-0000-0000-0000-000000000003', ownerId = '00000000-0000-0000-0000-000000000004';
const now = new Date().toISOString();
const identity = (userId, username, displayName) => ({ userId, username, displayName });

async function run(width, height) {
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
    // Alice's agent, shared read-write with the signed-in admin, who can't use its Runner; Ketty switched it to herself.
    const agent = { id: agentId, runnerId, ownerId, ownerName: 'Alice', access: 'Write', runnerName: 'runner-01', runnerState: 'Ready',
        runnerHarnessIds: ['claude'], runnerToolIds: ['vscode'], name: 'example-agent', harnessId: 'claude', workingDirectory: '/work/example-agent',
        state: 'Running', accountName: 'Team', loginOwnerId: ownerId, loginOwnerName: 'Alice', createdAt: now, updatedAt: now, runnerKind: 'Desktop',
        sharedWithMe: true, canUseRunner: false, gitIdentity: identity(kettyId, 'ketty', 'Ketty'), runningGitIdentity: identity(kettyId, 'ketty', 'Ketty') };
    // Stopped: the identity its next start uses counts, not the one it last ran with.
    const stopped = { ...agent, id: stoppedId, name: 'stopped-agent', workingDirectory: '/work/stopped-agent', state: 'Stopped', gitIdentity: identity(ownerId, 'alice', 'Alice') };
    const switches = [];
    await context.route('**/api/**', async route => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (path.endsWith('/icon') || path.endsWith('/session')) return route.fulfill({ status: 404 });
        if (path === `/api/agents/${agentId}/git-identity` && request.method() === 'POST') {
            switches.push(request.postDataJSON());
            return route.fulfill({ status: 204 });
        }
        const body = path === '/api/info' ? info : path === '/api/agents' ? [agent, stopped] : path === `/api/agents/${agentId}` ? agent
            : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') ? null : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    const openMenus = page.locator('.mud-popover-open .mud-menu-list');
    const menuTexts = async () => (await openMenus.first().locator('.mud-menu-item').allInnerTexts()).map(t => t.trim());
    const noPageOverflow = async label => assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${label}: no horizontal scroll`);

    await page.goto(base + '/agents');
    const row = page.locator('.agent-row', { hasText: 'example-agent' });
    await row.locator('.agent-account').waitFor({ timeout: 60000 });
    assert.match(await row.locator('.agent-account').innerText(), /Git: ketty/);
    assert.match(await page.locator('.agent-row', { hasText: 'stopped-agent' }).locator('.agent-account').innerText(), /Git on start: alice/);
    await row.locator('.agent-row-actions button').last().click();
    await openMenus.first().waitFor();
    assert.ok(!(await menuTexts()).some(t => t.includes('VS Code')), 'No VS Code from the list without Runner access');
    await noPageOverflow(`${width}px agents`);
    await page.screenshot({ path: `/tmp/dam-git-identity-list-${width}.png` });

    await page.goto(base + `/agents/${agentId}`);
    const context_ = page.locator('.terminal-context');
    await context_.waitFor({ timeout: 60000 });
    assert.match(await context_.innerText(), /Git: ketty/);
    await page.locator('.terminal-toolbar button[aria-label="More actions"]').click();
    await openMenus.first().waitFor();
    const entries = await menuTexts();
    assert.ok(entries.includes('Git identity…'), `Git identity in the menu (${entries.join(' | ')})`);
    assert.ok(!entries.some(t => t === 'VS Code' || t === 'Runner desktop'), 'No VS Code or desktop without Runner access');
    await openMenus.first().locator('.mud-menu-item', { hasText: 'Git identity' }).click();

    const dialog = page.locator('.mud-dialog', { hasText: 'Commits as' });
    await dialog.waitFor();
    const text = await dialog.innerText();
    assert.match(text, /Ketty \(ketty\)/);
    assert.match(text, /Owner\s+Alice/);
    // The admin manages conversations: themselves or anyone, but it isn't theirs to hand back.
    assert.equal(await dialog.getByRole('button', { name: `Commit as me (${me.username})` }).count(), 1);
    assert.equal(await dialog.getByRole('button', { name: 'Choose someone…' }).count(), 1);
    assert.equal(await dialog.getByRole('button', { name: /Hand back/ }).count(), 0);
    await noPageOverflow(`${width}px dialog`);
    await page.screenshot({ path: `/tmp/dam-git-identity-dialog-${width}.png` });

    await dialog.getByRole('button', { name: `Commit as me (${me.username})` }).click();
    const confirm = page.locator('.mud-dialog', { hasText: 'Switch and restart?' });
    await confirm.waitFor();
    assert.match(await confirm.innerText(), /restarts and resumes the conversation/);
    // Their identity goes to a Runner they can't use: they are told who can read it there.
    assert.match(await confirm.innerText(), /copied to runner-01/);
    await confirm.getByRole('button', { name: 'Switch' }).click();
    await dialog.waitFor({ state: 'detached' });
    assert.deepEqual(switches, [{ userId: me.id }]);
    assert.deepEqual(errors, []);
    await context.close();
}

try {
    await run(1400, 900);
    await run(390, 844);
    console.log(`Passed (${engine}): git identity in the agent list and terminal, the dialog's choices, an explicit confirmed switch, and no VS Code or desktop for read-write without Runner access, on desktop and phone.`);
} finally {
    await browser.close();
}
