
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';
// Shared videos play in the View dialog: the browser's own player on the preview link, its loading state, an error
// with Retry when the video can't be played (or its link expired), the original download, phones and both themes.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
// One second of VP8 (64x36, ffmpeg's test pattern): WebM plays in Chromium and Firefox builds without H.264.
const video = Buffer.from('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', 'base64');
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000010', now = new Date().toISOString();
    const files = ['night-forest.webm', 'storm.mp4'].map((fileName, i) => ({ id: `00000000-0000-0000-0000-00000000003${i}`, sessionId: id, fileName, caption: 'Test-only clip', bytes: video.length, sha256: 'a'.repeat(64), createdAt: now }));
    let links = 0, broken = true, slow = false;
    await context.route('**/shared-preview/**', async route => {
        const name = decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop());
        if (slow) await new Promise(resolve => setTimeout(resolve, 1100));
        if (name === 'storm.mp4' && broken) return route.fulfill({ status: 404, body: '' });
        return route.fulfill({ body: video, headers: { 'content-type': 'video/webm', 'cache-control': 'private, no-store' } });
    });
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        // The Manager's own state (build, CPU trace) comes from the test Manager: an empty list would break the Portal.
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        const file = files.find(file => path.startsWith(`/api/sessions/${id}/files/${file.id}`));
        // A new link each time, as the Manager gives (Retry gets one that hasn't expired).
        if (file && path.endsWith('/preview')) return route.fulfill({ json: { text: null, markdown: false, videoUrl: `/shared-preview/test-only-${++links}/view/${encodeURIComponent(file.fileName)}` } });
        if (file && path.endsWith('/download')) return route.fulfill({ body: video, headers: { 'content-disposition': `attachment; filename="${file.fileName}"` } });
        const body = path === '/api/info' ? info : path === `/api/sessions/${id}` ? { id, access: 'Read', ownerName: 'Test user', title: 'Files', harnessId: 'codex', messageCount: 0, createdAt: now, updatedAt: now }
            : path === `/api/sessions/${id}/files` ? files : path.endsWith('/content') ? { format: 'deve-session', version: 1, entries: [], sources: [] } : [];
        return route.fulfill({ json: body });
    });
    const dialog = page.locator('.file-preview-dialog');
    const player = dialog.locator('video.video-preview-player');
    for (const theme of ['dark', 'light']) {
        await page.emulateMedia({ colorScheme: theme });
        await page.evaluate(theme => localStorage.setItem('dam-theme', theme), theme);
        for (const width of [1280, 390, 320]) {
            await page.setViewportSize({ width, height: 850 });
            await page.goto(base + `/sessions/${id}`);
            await page.getByRole('button', { name: 'View night-forest.webm', exact: true }).click();
            await player.waitFor();
            await page.waitForFunction(() => (document.querySelector('video.video-preview-player') as HTMLVideoElement)?.readyState >= 1);
            await dialog.locator('.video-preview-loading').waitFor({ state: 'detached' });
            assert.equal(await player.getAttribute('controls'), '');
            assert.equal(await player.evaluate(v => getComputedStyle(v).visibility), 'visible');
            assert.ok(Math.abs(await player.evaluate((v: HTMLVideoElement) => v.duration) - 1) < 0.2, 'video duration');
            const box = await dialog.boundingBox(), shown = await player.boundingBox();
            assert.ok(box.x >= 0 && box.x + box.width <= width + 1, `viewer escapes ${width}`);
            assert.ok(shown.width > 0 && shown.x >= box.x && shown.x + shown.width <= box.x + box.width + 1, `player escapes the dialog at ${width}`);
            assert.ok(shown.height <= 850 * 0.65 + 1, 'player taller than the view');
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `page overflows ${width}/${theme}`);
            await page.screenshot({ path: `/tmp/dam-video-viewer-${theme}-${width}.png` });
            await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
            await dialog.waitFor({ state: 'detached' });
        }
    }
    // It plays, and the original downloads.
    await page.setViewportSize({ width: 390, height: 850 });
    await page.getByRole('button', { name: 'View night-forest.webm', exact: true }).click();
    await page.waitForFunction(() => (document.querySelector('video.video-preview-player') as HTMLVideoElement)?.readyState >= 1);
    await player.evaluate((v: HTMLVideoElement) => { v.muted = true; return v.play(); });
    await page.waitForFunction(() => (document.querySelector('video.video-preview-player') as HTMLVideoElement).currentTime > 0.3);
    const [download] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('link', { name: 'Download', exact: true }).click()]);
    assert.equal(download.suggestedFilename(), 'night-forest.webm');
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    await dialog.waitFor({ state: 'detached' });
    // A video that can't be played says so, keeps Download, and Retry asks for a new link.
    slow = true;
    await page.getByRole('button', { name: 'View storm.mp4', exact: true }).click();
    await dialog.locator('.video-preview-loading[aria-busy=true]').waitFor();
    await dialog.getByText(/Couldn't play this video/).waitFor();
    assert.equal(await player.evaluate(v => getComputedStyle(v).visibility), 'hidden');
    assert.equal(await dialog.getByRole('link', { name: 'Download', exact: true }).count(), 1);
    broken = false; slow = false;
    const before = links;
    await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
    await page.waitForFunction(() => (document.querySelector('video.video-preview-player') as HTMLVideoElement)?.readyState >= 1);
    await dialog.locator('.video-preview-loading').waitFor({ state: 'detached' });
    assert.ok(links > before, 'Retry asked for a new preview link');
    assert.ok((await player.getAttribute('src')).includes(`test-only-${links}/`));
    assert.equal(await page.getByText('Something went wrong in the Portal').isVisible(), false, 'no Portal error');
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): video playback in the browser's player, loading state, unplayable video with Retry for a new link, original download, 320px phone and both themes.`);
} finally { await browser.close(); }
