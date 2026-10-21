const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// The agent's task list (dam-tools tasks) in a dropdown next to Shared images and files: a progress ring on the button, a dot
// for an update not seen in this browser until the dropdown is closed, each task's state, % and times, Clear with an
// inline confirmation for read-write, and a dialog from the ⋮ menu when the toolbar has no room. Light and dark.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const agentId = '00000000-0000-0000-0000-000000000001';
const ago = minutes => new Date(Date.now() - minutes * 60000).toISOString();

async function run(theme) {
    const context = await browser.newContext({ viewport: { width: 1600, height: 900 }, colorScheme: theme });
    await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const now = ago(0);
    let access = 'Owner';
    const agent = () => ({ id: agentId, runnerId: agentId, ownerId: agentId, ownerName: 'Test user', access, runnerName: 'runner-01', runnerState: 'Ready',
        runnerHarnessIds: ['claude'], runnerToolIds: [], name: 'example-agent', harnessId: 'claude', workingDirectory: '/work/example',
        state: 'Running', createdAt: now, updatedAt: now, canUseRunner: true });
    let tasks = { revision: 3, updatedAt: ago(2), tasks: [
        { id: 1, name: 'Stream exports to a temporary file', percent: 100, description: 'Exports and harness switches no longer build one large string.',
          startedAt: ago(42), finishedAt: ago(30) },
        { id: 2, name: 'Quiet hidden tabs', percent: 60, description: 'Sections pause while their tab is hidden\nand catch up once shown.',
          startedAt: ago(25), leftSeconds: 900, leftAt: ago(5) },
        { id: 3, name: 'Write the task tracker docs', percent: 0, description: '' }] };
    let clears = 0;
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/info' || path === '/api/diagnostics/cpu') return route.continue();
        if (path === `/api/agents/${agentId}/tasks`) {
            if (route.request().method() === 'DELETE') {
                clears++;
                tasks = { revision: tasks.revision + 1, updatedAt: ago(0), tasks: [] };
                return route.fulfill({ status: 204 });
            }
            return route.fulfill({ json: tasks });
        }
        if (path.endsWith('/session') || path.endsWith('/icon')) return route.fulfill({ status: 404 });
        const body = path === `/api/agents/${agentId}` ? agent() : path === '/api/agents' ? [agent()]
            : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') ? null : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' })));
    const button = page.locator('[data-toolbar="tasks"] button');
    const dot = page.locator('[data-toolbar="tasks"] .tasks-update-dot');
    const ring = page.locator('[data-toolbar="tasks"] .tasks-progress-value');
    // Loaded once the button's label says so (it names the done count and whether there is an unseen update).
    const labelled = label => page.waitForFunction(label =>
        document.querySelector('[data-toolbar="tasks"] button')?.getAttribute('aria-label') === label, label, { timeout: 60000 });
    const panel = page.locator('.mud-popover-open .tasks-panel');

    await page.goto(base + `/agents/${agentId}`);
    await labelled('Tasks: 1 of 3 done, updated');
    assert.equal(await button.getAttribute('title'), 'Tasks · 1 of 3 done · 53%');
    assert.equal(await ring.getAttribute('stroke-dasharray'), '53 100', 'The ring shows the overall progress');
    assert.equal(await dot.count(), 1, 'Not seen in this browser yet: the dot shows');

    // A dropdown, not a dialog: overall progress, then every task with its state, % and times.
    await button.click();
    await panel.waitFor();
    assert.equal(await page.locator('.mud-dialog').count(), 0);
    const text = await panel.innerText();
    assert.match(text, /Tasks[\s\S]*53%/);
    assert.match(text, /1 of 3 done · 42m taken · ~(9|10)m left/);
    assert.match(text, /Stream exports to a temporary file[\s\S]*100%[\s\S]*12m taken/);
    assert.match(text, /Quiet hidden tabs[\s\S]*60%[\s\S]*and catch up once shown\.[\s\S]*25m so far · ~(9|10)m left/);
    assert.equal(await panel.locator('.task-done').count(), 1);
    assert.equal(await panel.locator('.task-active .task-ring').count(), 1);
    assert.equal(await panel.locator('.task-pending').count(), 1);
    assert.match(text, /Updated 2 minutes ago/);
    await panel.screenshot({ path: `/tmp/dam-agent-tasks-${theme}.png` });
    await page.locator('[data-toolbar="tasks"]').screenshot({ path: `/tmp/dam-agent-tasks-button-${theme}.png` });

    // Closing it marks what it showed as seen, also after a reload.
    await button.click();
    await panel.waitFor({ state: 'detached' });
    await dot.waitFor({ state: 'detached' });
    await labelled('Tasks: 1 of 3 done');
    await page.reload();
    await labelled('Tasks: 1 of 3 done');
    assert.equal(await dot.count(), 0, 'Seen stays seen');

    // An update (a new revision) shows the dot again.
    tasks = { ...tasks, revision: 4, tasks: tasks.tasks.map(t => t.id === 2 ? { ...t, percent: 100, finishedAt: ago(0) } : t) };
    await page.reload();
    await labelled('Tasks: 2 of 3 done, updated');
    assert.equal(await dot.count(), 1);
    assert.equal(await ring.getAttribute('stroke-dasharray'), '67 100');

    // Clear, confirmed inline; afterwards the list is empty and nothing is marked.
    await button.click();
    await panel.waitFor();
    await panel.getByRole('button', { name: 'Clear', exact: true }).click();
    await panel.getByText('Clear all tasks?').waitFor();
    await panel.getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(clears, 0);
    await panel.getByRole('button', { name: 'Clear', exact: true }).click();
    await panel.locator('.tasks-confirm').getByRole('button', { name: 'Clear', exact: true }).click();
    await panel.getByText('No tasks yet').waitFor();
    assert.equal(clears, 1);
    await button.click();
    await panel.waitFor({ state: 'detached' });
    await labelled('Tasks');
    assert.equal(await dot.count(), 0);
    assert.equal(await page.locator('[data-toolbar="tasks"] .tasks-progress').count(), 0, 'No ring without tasks');

    // Read-only: the list without Clear.
    access = 'Read';
    tasks = { revision: 9, updatedAt: ago(1), tasks: [{ id: 1, name: 'Review', percent: 30, description: '', startedAt: ago(3) }] };
    await page.reload();
    await labelled('Tasks: 0 of 1 done, updated');
    await button.click();
    await panel.getByText('Review').waitFor();
    assert.equal(await panel.getByRole('button', { name: 'Clear', exact: true }).count(), 0);
    await button.click();

    // A phone: from the ⋮ menu, as a dialog.
    access = 'Owner';
    await page.setViewportSize({ width: 360, height: 760 });
    await page.waitForTimeout(400);
    await page.locator('.terminal-toolbar button[aria-label="More actions"]').click();
    const entry = page.locator('.mud-popover-open .mud-menu-item', { hasText: 'Tasks (0/1)' });
    await entry.waitFor();
    await entry.click();
    const dialog = page.locator('.mud-dialog .tasks-panel');
    await dialog.waitFor();
    assert.equal(await page.locator('.mud-popover-open .mud-menu-list').count(), 0, 'The ⋮ menu closes when the dialog opens');
    assert.match(await dialog.innerText(), /Review[\s\S]*30%/);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Phone: no horizontal scroll');
    await page.screenshot({ path: `/tmp/dam-agent-tasks-phone-${theme}.png` });
    await page.locator('.mud-dialog').getByRole('button', { name: 'Close', exact: true }).click();
    assert.deepEqual(errors, []);
    await context.close();
}

try {
    await run('light');
    await run('dark');
    console.log(`Passed (${engine}): tasks dropdown with a progress ring and an update dot until closed, states, % and times, inline Clear for read-write only, and the ⋮ menu dialog on a phone, light and dark.`);
} finally {
    await browser.close();
}
