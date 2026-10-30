import assert from 'node:assert/strict';
// Deleting an agent lists what it affects (its folder's size and unsaved Git work, and its conversations, which stay)
// and offers to delete the agent only or the agent and its folder. A folder that can't be deleted says why.
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5109';
const browser = await playwright[process.env.DAM_TEST_BROWSER ?? 'chromium'].launch();
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const me = await (await context.request.get(base + '/api/me')).json();
    const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
    const now = new Date().toISOString();
    const folder = '/home/runner/work/admin/investigatecrashes';
    const agent = { id: id(10), runnerId: id(1), ownerId: me.id, ownerName: 'Test user', access: 'Owner', runnerName: 'runner-02', runnerState: 'Ready',
        runnerHarnessIds: ['pi'], runnerToolIds: [], name: 'investigatecrashes', harnessId: 'pi', workingDirectory: folder, state: 'Stopped',
        loginOwnerId: me.id, loginOwnerName: 'Test user', createdAt: now, updatedAt: now, projectId: id(10), projectName: 'investigatecrashes',
        isClone: false, canUseRunner: true, workspaces: 0 };
    const full = { path: folder, conversations: 2, checked: true, exists: true, blocked: null, git: true, uncommitted: 4,
        sample: ['crash.log', 'src/main.c', 'notes.md'], unpushed: 2, gitProblem: null };
    let deletion: Record<string, any> = full, slowDeletion = false, failSize = false, failDelete = false;
    const size = { bytes: Math.round(1.2 * 2 ** 30), freedBytes: Math.round(0.9 * 2 ** 30), files: 12345 };
    const deletes: string[] = [];
    await context.route('**/api/**', async route => {
        const url = new URL(route.request().url()), path = url.pathname, method = route.request().method();
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/info' || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (method === 'DELETE' && path === `/api/agents/${id(10)}`) {
            if (failDelete) return route.fulfill({ status: 409, json: { detail: 'Test-only: the folder is busy.' } });
            deletes.push(path + url.search);
            return route.fulfill({ status: 204 });
        }
        if (path === `/api/agents/${id(10)}/deletion`) {
            if (slowDeletion) await new Promise(resolve => setTimeout(resolve, 900));
            return route.fulfill({ json: deletion });
        }
        if (path === `/api/agents/${id(10)}/folder-size`) {
            if (failSize) return route.fulfill({ status: 503, json: { detail: 'Test-only measurement failure' } });
            return route.fulfill({ json: size });
        }
        if (path === '/api/agents') return route.fulfill({ json: [agent] });
        if (path === `/api/agents/${id(10)}`) return route.fulfill({ json: agent });
        if (method !== 'GET') return route.fulfill({ status: 204 });
        return route.fulfill({ json: path.endsWith('/limit') || path.endsWith('/session') ? null : [] });
    });

    const dialog = page.getByRole('dialog');
    const open = async () => {
        await page.goto(base + '/agents');
        const more = page.locator('.agent-row .agent-row-actions .mud-menu button').first();
        await more.waitFor({ timeout: 60000 });
        errors.length = 0;
        await more.click();
        await page.getByRole('menuitem', { name: 'Delete', exact: true }).click();
        await dialog.getByText('Delete “investigatecrashes”?', { exact: true }).waitFor();
    };
    const button = name => dialog.getByRole('button', { name, exact: true });

    // Everything it affects, and both choices.
    slowDeletion = true;
    await open();
    await dialog.locator('[role="status"][aria-busy="true"]').first().waitFor({ state: 'attached' });
    assert.equal(await button('Delete agent and folder').isDisabled(), true, 'nothing can be deleted before the folder is checked');
    slowDeletion = false;
    await dialog.getByText(folder, { exact: true }).waitFor();
    await dialog.getByText('1.2 GB on disk · 12,345 files · frees about 921.6 MB', { exact: true }).waitFor();
    await dialog.getByText('4 files have uncommitted changes (crash.log, src/main.c, notes.md, +1)', { exact: true }).waitFor();
    await dialog.getByText('2 commits aren\'t on any remote branch', { exact: true }).waitFor();
    await dialog.getByText(/Its 2 conversations stay in the Manager's history\./).waitFor();
    assert.equal(await button('Delete agent only').isEnabled(), true);
    assert.equal(await button('Delete agent and folder').isEnabled(), true);
    await page.screenshot({ path: '/tmp/dam-delete-agent.png' });
    await button('Delete agent and folder').click();
    await dialog.waitFor({ state: 'hidden' });
    assert.deepEqual(deletes, [`/api/agents/${id(10)}?deleteFolder=true`]);
    await page.getByText('Deleted \'investigatecrashes\' and its folder', { exact: true }).waitFor();

    // A failed delete stays in the dialog and says why.
    failDelete = true;
    await open();
    await button('Delete agent and folder').click();
    await dialog.getByText('Test-only: the folder is busy.', { exact: true }).waitFor();
    failDelete = false;

    // A folder another agent works in: only the agent can be deleted.
    deletion = { ...full, blocked: 'Agent \'nested\' works in this folder or in a folder inside it.' };
    await open();
    await dialog.getByText(/Only the agent can be deleted\. Agent 'nested' works in this folder/).waitFor();
    assert.equal(await button('Delete agent and folder').isDisabled(), true, 'a shared folder is never deleted');
    await button('Delete agent only').click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(deletes.at(-1), `/api/agents/${id(10)}`);
    await page.getByText('Deleted \'investigatecrashes\'', { exact: true }).waitFor();

    // A Runner that isn't ready: the folder can't be checked, the agent alone can still be deleted.
    deletion = { ...full, checked: false, exists: false, git: false, uncommitted: 0, sample: [], unpushed: 0,
        blocked: 'Runner \'runner-02\' isn\'t ready (Stopped), so its folder can\'t be checked or deleted.' };
    await open();
    await dialog.getByText(/isn't ready \(Stopped\)/).waitFor();
    assert.equal(await dialog.getByText(/on disk/).count(), 0, 'no size for a folder that wasn\'t checked');
    assert.equal(await button('Delete agent and folder').isDisabled(), true);
    assert.equal(await button('Delete agent only').isEnabled(), true);
    await page.keyboard.press('Escape');

    // Already gone: there is only the agent to delete.
    deletion = { ...full, exists: false, git: false, uncommitted: 0, sample: [], unpushed: 0 };
    await open();
    await dialog.getByText('It is already gone.', { exact: true }).waitFor();
    assert.equal(await button('Delete agent and folder').count(), 0);
    await button('Delete agent').waitFor();
    await page.keyboard.press('Escape');

    // A failed measurement offers Retry; a plain folder warns that nothing in it is pushed.
    failSize = true;
    deletion = { ...full, git: false, uncommitted: 0, sample: [], unpushed: 0 };
    await open();
    await dialog.getByText('Not a Git repository: nothing in it is pushed anywhere.', { exact: true }).waitFor();
    await dialog.getByText('Couldn\'t measure the folder.', { exact: false }).waitFor();
    failSize = false;
    await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
    await dialog.getByText(/12,345 files/).waitFor();
    await page.keyboard.press('Escape');

    // Phones, in the dark theme: everything fits, and the three buttons wrap instead of running off screen.
    deletion = full;
    await context.addInitScript(() => localStorage.setItem('dam-theme', 'dark'));
    // At 320px the row menu itself flips between above and below its button, so the dialog is opened at 390px.
    await page.setViewportSize({ width: 390, height: 800 });
    await open();
    await dialog.getByText(/12,345 files/).waitFor();
    for (const width of [390, 320]) {
        await page.setViewportSize({ width, height: 800 });
        await page.waitForTimeout(300);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}px: the page scrolls sideways`);
        for (const name of ['Cancel', 'Delete agent only', 'Delete agent and folder']) {
            const box = (await button(name).boundingBox())!;
            assert.ok(box.x >= 0 && box.x + box.width <= width, `${width}px: ${name} is on screen`);
        }
        await page.screenshot({ path: `/tmp/dam-delete-agent-${width}.png` });
    }
    await page.keyboard.press('Escape');
    assert.deepEqual(errors, []);
    console.log('Passed: delete preview (folder, size, unsaved work, conversations), agent only and agent with folder, blocked, offline and missing folders, failures with Retry, loading, phone layouts in the dark theme.');
} finally {
    await browser.close();
}
