import assert from 'node:assert/strict';
// Shared files and images of a project's other workspaces show by default, saying where they came from. "This workspace"
// hides them and their counts, in both lists, for this tab only: a reload keeps it, a new tab shows everything again.
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const browser = await playwright[process.env.DAM_TEST_BROWSER ?? 'chromium'].launch();
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const errors: string[] = [];
    const watch = (p: import('playwright').Page) => {
        p.on('pageerror', error => errors.push(error.message));
        p.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
        return p;
    };
    let page = watch(await context.newPage());
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);

    const info = await (await context.request.get(base + '/api/info')).json();
    const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
    const agentId = id(1), sessionId = id(10);
    const at = minutes => new Date(Date.now() - minutes * 60000).toISOString();
    const file = (n, fileName, otherWorkspace: string | null = null) =>
        ({ id: id(n), sessionId, fileName, caption: '', bytes: 2048, sha256: '0'.repeat(64), createdAt: at(n), otherWorkspace });
    const image = (n, caption, otherWorkspace: string | null = null) =>
        ({ id: id(n), sessionId, fileName: `${caption}.png`, caption, contentType: 'image/png', bytes: 4096, createdAt: at(n), otherWorkspace });
    const allFiles = [file(11, 'own-report.md'), file(12, 'toolbar-build.apk', 'fix-toolbar'), file(13, 'old-notes.zip', 'experiment (removed)')];
    let files = allFiles;
    const images = [image(21, 'Own chart'), image(22, 'Toolbar screenshot', 'fix-toolbar')];
    const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
    const now = new Date().toISOString();
    const agent = { id: agentId, runnerId: agentId, runnerName: 'Test runner', runnerState: 'Ready', runnerHarnessIds: ['codex'], runnerToolIds: [],
        name: 'Release builds', harnessId: 'codex', workingDirectory: '/work/example', state: 'Running', access: 'Owner', createdAt: now, updatedAt: now };
    const session = { id: sessionId, ownerId: agentId, ownerName: 'Test user', access: 'Owner', title: 'Release builds', harnessId: 'codex',
        messageCount: 0, createdAt: now, updatedAt: now };
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/diagnostics/cpu') return route.continue();
        if (path === `/api/sessions/${sessionId}/files`) return route.fulfill({ json: files });
        if (path === `/api/sessions/${sessionId}/images`) return route.fulfill({ json: images });
        if (path.startsWith(`/api/sessions/${sessionId}/images/`)) return route.fulfill({ body: png, contentType: 'image/png' });
        if (path === `/api/sessions/${sessionId}`) return route.fulfill({ json: session });
        if (path === `/api/sessions/${sessionId}/content`) return route.fulfill({ json: { format: 'deve-session', version: 1, entries: [], sources: [] } });
        const body = path === '/api/info' ? info : path === `/api/agents/${agentId}` ? agent : path === `/api/agents/${agentId}/session` ? session
            : path === '/api/agents' ? [agent] : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') ? null : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));

    const dialog = () => page.getByRole('dialog');
    const close = async () => { await dialog().getByRole('button', { name: 'Close', exact: true }).last().click(); await dialog().waitFor({ state: 'detached' }); };
    const names = () => dialog().locator('.session-file-name').allTextContents();
    const openAgent = async () => {
        await page.goto(base + `/agents/${agentId}`);
        await page.locator('.terminal-toolbar button[aria-label^="Shared files ("]').waitFor({ timeout: 60000 });
    };
    const counts = async () => [await page.locator('.terminal-toolbar button[aria-label^="Shared files ("]').getAttribute('aria-label'),
        await page.locator('.terminal-toolbar button[aria-label^="Shared images ("]').getAttribute('aria-label')];

    // By default: everything, with where it came from.
    await openAgent();
    await page.getByRole('button', { name: 'Shared files (3)', exact: true }).waitFor();
    assert.deepEqual(await counts(), ['Shared files (3)', 'Shared images (2)']);
    await page.getByRole('button', { name: 'Shared files (3)', exact: true }).click();
    await dialog().getByText('All workspaces 3', { exact: true }).waitFor();
    await dialog().getByText('This workspace 1', { exact: true }).waitFor();
    assert.deepEqual(await names(), allFiles.map(f => f.fileName));
    const meta = dialog().locator('.session-file-meta');
    assert.doesNotMatch(await meta.nth(0).textContent() ?? '', /from /, 'this workspace\'s own file says nothing extra');
    assert.match(await meta.nth(1).textContent() ?? '', / · from fix-toolbar · /);
    assert.match(await meta.nth(2).textContent() ?? '', / · from experiment \(removed\) · /);

    // This workspace: the list and both toolbar counts.
    await dialog().getByText('This workspace 1', { exact: true }).click();
    await dialog().getByText('toolbar-build.apk', { exact: true }).waitFor({ state: 'detached' });
    assert.deepEqual(await names(), ['own-report.md']);
    const chosen = await dialog().locator('.shared-scope .mud-chip-selected').allTextContents();
    assert.deepEqual(chosen.map(text => text.trim()), ['This workspace 1'], 'the chosen one is marked');
    const [filled, plain] = await Promise.all(['This workspace 1', 'All workspaces 3'].map(text => dialog().locator('.shared-scope .mud-chip', { hasText: text })
        .evaluate(chip => getComputedStyle(chip).backgroundColor)));
    assert.notEqual(filled, plain, 'the chosen one looks different');
    await page.screenshot({ path: '/tmp/dam-shared-workspaces-files.png' });
    await close();
    assert.deepEqual(await counts(), ['Shared files (1)', 'Shared images (1)']);

    // Shared images follow the same choice, and can switch back.
    await page.getByRole('button', { name: 'Shared images (1)', exact: true }).click();
    await dialog().getByText('This workspace 1', { exact: true }).waitFor();
    assert.deepEqual(await dialog().locator('.session-image-card > span').allTextContents(), ['Own chart']);
    await dialog().getByText('All workspaces 2', { exact: true }).click();
    await dialog().getByText('Toolbar screenshot', { exact: true }).waitFor();
    assert.match(await dialog().locator('.session-image-card small').nth(1).textContent() ?? '', / · from fix-toolbar$/);
    await dialog().getByText('This workspace 1', { exact: true }).click();
    await dialog().getByText('Toolbar screenshot', { exact: true }).waitFor({ state: 'detached' });
    await close();

    // A reload keeps it, and so does the conversation page.
    await openAgent();
    await page.getByRole('button', { name: 'Shared files (1)', exact: true }).waitFor();
    await page.goto(base + `/sessions/${sessionId}`);
    await page.getByText('own-report.md', { exact: true }).waitFor({ timeout: 60000 });
    assert.equal(await page.getByText('toolbar-build.apk', { exact: true }).count(), 0);
    await page.getByText('This workspace 1', { exact: true }).first().waitFor();

    // Nothing of its own: it says so, and the choice stays there to switch back.
    files = allFiles.slice(1);
    await openAgent();
    await page.getByRole('button', { name: 'Shared files (0)', exact: true }).click();
    await dialog().getByText('No shared files in this workspace yet.', { exact: true }).waitFor();
    await dialog().getByText('All workspaces 2', { exact: true }).waitFor();
    await close();

    // Only its own: no choice to make.
    files = allFiles.slice(0, 1);
    await openAgent();
    await page.getByRole('button', { name: 'Shared files (1)', exact: true }).click();
    await dialog().getByText('own-report.md', { exact: true }).waitFor();
    assert.equal(await dialog().locator('.shared-scope').count(), 0);
    await close();

    // A new tab shows all workspaces again.
    files = allFiles;
    await page.close();
    page = watch(await context.newPage());
    await openAgent();
    await page.getByRole('button', { name: 'Shared files (3)', exact: true }).waitFor();
    assert.deepEqual(await counts(), ['Shared files (3)', 'Shared images (2)']);

    // Phones, in the dark theme: from the ⋮ menu, and the choice fits.
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.setViewportSize({ width: 320, height: 700 });
    await page.waitForTimeout(150);
    await page.locator('.terminal-toolbar button[aria-label="More actions"]').click();
    await page.getByRole('menuitem', { name: 'Shared files (3)', exact: true }).click();
    await dialog().getByText('This workspace 1', { exact: true }).click();
    await dialog().getByText('toolbar-build.apk', { exact: true }).waitFor({ state: 'detached' });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'the page scrolls sideways');
    for (const chip of ['All workspaces 3', 'This workspace 1']) {
        const box = (await dialog().getByText(chip, { exact: true }).boundingBox())!;
        assert.ok(box.x >= 0 && box.x + box.width <= 320, `${chip} is on screen`);
    }
    await page.screenshot({ path: '/tmp/dam-shared-workspaces-320.png' });
    await close();

    assert.deepEqual(errors, []);
    console.log('Passed: other workspaces\' shared files and images show by default with where they came from; This workspace filters both lists and counts for the tab, survives a reload, resets in a new tab; empty and single-workspace cases; phone in the dark theme.');
} finally {
    await browser.close();
}
