const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Files agents share (dam-tools share-file): the conversation page's list, the terminal toolbar button and the phone menu.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
    const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);

    const info = await (await context.request.get(base + '/api/info')).json();
    const agentId = '00000000-0000-0000-0000-000000000001', sessionId = '00000000-0000-0000-0000-000000000010';
    const now = new Date().toISOString();
    const hex = seed => [...Array(64)].map((_, i) => '0123456789abcdef'[(seed * 7 + i * 13) % 16]).join('');
    const allFiles = [
        { id: '00000000-0000-0000-0000-000000000011', sessionId, fileName: 'app-release.apk', caption: 'Debug build with the new login screen', bytes: 12_998_144, sha256: hex(1), createdAt: now },
        { id: '00000000-0000-0000-0000-000000000012', sessionId, fileName: 'DeveAgentManager-Setup-1.0.128.exe', caption: '', bytes: 84_213_000, sha256: hex(2), createdAt: new Date(Date.now() - 5 * 60000).toISOString() },
        { id: '00000000-0000-0000-0000-000000000013', sessionId, fileName: 'build-artifacts-for-the-windows-android-and-linux-clients-of-this-release.zip', caption: 'Everything the release workflow produced, for comparing against the published version', bytes: 512, sha256: hex(3), createdAt: new Date(Date.now() - 3600000).toISOString() },
    ];
    let files = [...allFiles];
    let access = 'Owner';
    const deleted = [];
    const agent = { id: agentId, runnerId: agentId, runnerName: 'Test runner', runnerState: 'Ready', runnerHarnessIds: ['codex'], runnerToolIds: [],
        name: 'Release builds', harnessId: 'codex', workingDirectory: '/work/example', state: 'Running', access: 'Owner', createdAt: now, updatedAt: now };
    const session = () => ({ id: sessionId, ownerId: agentId, ownerName: 'Test user', access, title: 'Release builds', harnessId: 'codex', messageCount: 0, createdAt: now, updatedAt: now });
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname, method = route.request().method();
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/diagnostics/cpu') return route.continue();
        if (path === `/api/sessions/${sessionId}/files`) return route.fulfill({ json: files });
        const file = files.find(f => path.startsWith(`/api/sessions/${sessionId}/files/${f.id}`));
        if (file && method === 'DELETE') { files = files.filter(f => f !== file); deleted.push(file.id); return route.fulfill({ status: 204 }); }
        if (file && path.endsWith('/download')) return route.fulfill({ status: 200, body: 'test-only build',
            headers: { 'content-type': 'application/octet-stream', 'content-disposition': `attachment; filename="${file.fileName}"` } });
        if (path === `/api/sessions/${sessionId}`) return route.fulfill({ json: session() });
        if (path === `/api/sessions/${sessionId}/content`) return route.fulfill({ json: { format: 'deve-session', version: 1, entries: [], sources: [] } });
        const body = path === '/api/info' ? info : path === `/api/agents/${agentId}` ? agent : path === `/api/agents/${agentId}/session` ? session()
            : path === '/api/agents' ? [agent] : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 } : path.endsWith('/limit') ? null : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    const noOverflow = async label => assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${label} overflows`);

    // The conversation page: newest first, what it is, how big, its checksum, a download and (for the owner) delete.
    for (const scheme of ['dark', 'light']) {
        await page.emulateMedia({ colorScheme: scheme });
        for (const width of [1280, 390, 320]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(base + `/sessions/${sessionId}`);
            const rows = page.locator('.session-file');
            await rows.first().waitFor({ timeout: 60000 });
            assert.deepEqual(await rows.locator('.session-file-name').allTextContents(), allFiles.map(f => f.fileName));
            assert.equal(await rows.first().locator('.session-file-caption').textContent(), allFiles[0].caption);
            assert.equal(await rows.nth(1).locator('.session-file-caption').count(), 0, 'no caption, no empty line');
            assert.match(await rows.first().locator('.session-file-meta').textContent(), new RegExp(`^12\\.4 MiB · .+ · SHA-256 ${hex(1).slice(0, 12)}…$`));
            assert.match(await rows.nth(2).locator('.session-file-meta').textContent(), /^512 bytes · /);
            assert.equal(await rows.first().locator('.session-file-meta .mono').getAttribute('title'), `SHA-256 ${hex(1)}`);
            const link = page.getByRole('link', { name: 'Download app-release.apk', exact: true });
            assert.ok((await link.getAttribute('href')).endsWith(`/sessions/${sessionId}/files/${allFiles[0].id}/download`));
            assert.equal(await link.getAttribute('download'), 'app-release.apk');
            await noOverflow(`conversation page at ${width}`);
            for (let i = 0; i < 3; i++) {
                const [row, button] = [await rows.nth(i).boundingBox(), await rows.nth(i).getByRole('link').boundingBox()];
                assert.ok(button.x >= row.x && button.x + button.width <= row.x + row.width + 0.5, `download button escapes its row at ${width}`);
                if (width <= 520) {
                    const name = await rows.nth(i).locator('.session-file-name').boundingBox();
                    assert.ok(button.y >= name.y + name.height && button.width > row.width / 2, `phones get a wide download button under the name (${width})`);
                }
            }
            await page.screenshot({ path: `/tmp/dam-shared-files-${scheme}-${width}.png`, fullPage: true });
        }
    }
    await page.emulateMedia({ colorScheme: 'dark' });

    // Downloading saves the file and leaves the page where it is.
    const before = page.url();
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Download app-release.apk', exact: true }).click()]);
    assert.equal(download.suggestedFilename(), 'app-release.apk');
    assert.equal(page.url(), before);

    // Deleting asks first; cancelling keeps it.
    const setup = allFiles[1].fileName;
    await page.getByRole('button', { name: `Delete ${setup}`, exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.deepEqual(deleted, []);
    await page.getByRole('button', { name: `Delete ${setup}`, exact: true }).click();
    await page.getByRole('dialog').getByText("can't be undone").waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await page.getByText(setup, { exact: true }).waitFor({ state: 'detached' });
    assert.deepEqual(deleted, [allFiles[1].id]);

    // Read-only: download only.
    access = 'Read';
    await page.reload();
    await page.locator('.session-file').first().waitFor({ timeout: 60000 });
    assert.equal(await page.getByRole('button', { name: /^Delete / }).count(), 0);
    access = 'Owner';

    // The terminal toolbar: a button with a count, only once files exist.
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base + `/agents/${agentId}`);
    const toolbarButton = page.getByRole('button', { name: 'Shared files (2)', exact: true });
    await toolbarButton.waitFor({ timeout: 60000 });
    assert.equal(await page.locator('.terminal-toolbar .mud-badge').filter({ hasText: '2' }).count() >= 1, true);
    await toolbarButton.click();
    await page.getByRole('dialog').getByRole('link', { name: 'Download app-release.apk', exact: true }).waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.getByRole('dialog').waitFor({ state: 'detached' });

    // Narrow phones: in the ⋮ menu, and the list fits.
    await page.setViewportSize({ width: 320, height: 700 });
    await page.waitForTimeout(150);
    assert.equal(await toolbarButton.isVisible(), false);
    await page.locator('.terminal-toolbar button[aria-label="More actions"]').click();
    await page.getByRole('menuitem', { name: 'Shared files (2)', exact: true }).click();
    await page.getByRole('dialog').getByRole('link', { name: 'Download app-release.apk', exact: true }).waitFor();
    await noOverflow('files dialog on a phone');
    const dialog = await page.getByRole('dialog').boundingBox();
    assert.ok(dialog.x >= 0 && dialog.x + dialog.width <= 321, 'files dialog fits the phone');
    await page.screenshot({ path: '/tmp/dam-shared-files-dialog-320.png' });
    await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();

    // Nothing shared: the button is still there, like Shared images, without a count; the conversation page says so.
    files = [];
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(base + `/agents/${agentId}`);
    const empty = page.locator('.terminal-toolbar button[aria-label="Shared files (0)"]');
    await empty.waitFor({ timeout: 60000 });
    assert.equal(await page.locator('[data-toolbar="files"] .mud-badge').filter({ hasText: /\d/ }).count(), 0, 'no count without files');
    await empty.click();
    await page.getByRole('dialog').getByText('No shared files yet.', { exact: true }).waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.goto(base + `/sessions/${sessionId}`);
    await page.getByText('No shared files yet.', { exact: true }).waitFor({ timeout: 60000 });

    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): shared files list newest first with size, caption and checksum; attachment downloads without navigating; confirmed owner-only delete; toolbar button always there, counted when files exist; phone menu, layout and light/dark themes.`);
} finally {
    await browser.close();
}
