const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';
// Shared audio plays in the View dialog: the browser's own player on the preview link, its loading state, an error
// with Retry when the file can't be played (or its link expired), the original download, phones and both themes.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
// One second of a 440 Hz tone as 8 kHz 16-bit PCM WAV, which every browser plays.
const rate = 8000;
const samples = Buffer.alloc(rate * 2);
for (let i = 0; i < rate; i++) samples.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * i / rate) * 8000), i * 2);
const header = Buffer.alloc(44);
header.write('RIFF', 0); header.writeUInt32LE(36 + samples.length, 4); header.write('WAVEfmt ', 8);
header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22); header.writeUInt32LE(rate, 24);
header.writeUInt32LE(rate * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34); header.write('data', 36); header.writeUInt32LE(samples.length, 40);
const audio = Buffer.concat([header, samples]);
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
    const id = '00000000-0000-0000-0000-000000000011', now = new Date().toISOString();
    const files = ['tone.wav', 'voice memo.ogg'].map((fileName, i) => ({ id: `00000000-0000-0000-0000-00000000004${i}`, sessionId: id, fileName, caption: 'Test-only sound', bytes: audio.length, sha256: 'a'.repeat(64), createdAt: now }));
    let links = 0, broken = true, slow = false;
    await context.route('**/shared-preview/**', async route => {
        const name = decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop());
        if (slow) await new Promise(resolve => setTimeout(resolve, 1100));
        if (name === 'voice memo.ogg' && broken) return route.fulfill({ status: 404, body: '' });
        return route.fulfill({ body: audio, headers: { 'content-type': 'audio/wav', 'cache-control': 'private, no-store' } });
    });
    await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        // The Manager's own state (build, CPU trace) comes from the test Manager: an empty list would break the Portal.
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        const file = files.find(file => path.startsWith(`/api/sessions/${id}/files/${file.id}`));
        // A new link each time, as the Manager gives (Retry gets one that hasn't expired).
        if (file && path.endsWith('/preview')) return route.fulfill({ json: { text: null, markdown: false, audioUrl: `/shared-preview/test-only-${++links}/view/${encodeURIComponent(file.fileName)}` } });
        if (file && path.endsWith('/download')) return route.fulfill({ body: audio, headers: { 'content-disposition': `attachment; filename="${file.fileName}"` } });
        const body = path === '/api/info' ? info : path === `/api/sessions/${id}` ? { id, access: 'Read', ownerName: 'Test user', title: 'Files', harnessId: 'codex', messageCount: 0, createdAt: now, updatedAt: now }
            : path === `/api/sessions/${id}/files` ? files : path.endsWith('/content') ? { format: 'deve-session', version: 1, entries: [], sources: [] } : [];
        return route.fulfill({ json: body });
    });
    const dialog = page.locator('.file-preview-dialog');
    const player = dialog.locator('.audio-preview-player audio');
    const ready = () => page.waitForFunction(() => (document.querySelector('.audio-preview-player audio') as HTMLAudioElement)?.readyState >= 1);
    // The dialog opens with an animation: measure once it stopped moving.
    const settled = async () => {
        for (let last = '', i = 0; i < 50; i++) {
            const now = JSON.stringify(await dialog.boundingBox());
            if (now === last) return;
            last = now;
            await page.waitForTimeout(100);
        }
    };
    // Audio files have their own icon in the list.
    await page.goto(base + `/sessions/${id}`);
    await page.getByRole('button', { name: 'View tone.wav', exact: true }).waitFor();
    for (const theme of ['dark', 'light']) {
        await page.emulateMedia({ colorScheme: theme });
        await page.evaluate(theme => localStorage.setItem('dam-theme', theme), theme);
        for (const width of [1280, 390, 320]) {
            await page.setViewportSize({ width, height: 850 });
            await page.goto(base + `/sessions/${id}`);
            await page.getByRole('button', { name: 'View tone.wav', exact: true }).click();
            await player.waitFor({ state: 'attached' });
            await ready();
            await dialog.locator('.audio-preview-loading').waitFor({ state: 'detached' });
            assert.equal(await player.getAttribute('controls'), '');
            assert.equal(await player.evaluate(a => getComputedStyle(a.parentElement).visibility), 'visible');
            assert.ok(Math.abs(await player.evaluate((a: HTMLAudioElement) => a.duration) - 1) < 0.1, 'audio duration');
            await settled();
            const box = await dialog.boundingBox(), shown = await player.boundingBox();
            assert.ok(box.x >= 0 && box.x + box.width <= width + 1, `viewer escapes ${width}`);
            assert.ok(shown.width > 100 && shown.x >= box.x && shown.x + shown.width <= box.x + box.width + 1, `player escapes the dialog at ${width}`);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `page overflows ${width}/${theme}`);
            if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: `${process.env.DAM_SCREENSHOTS}/audio-viewer-${engine}-${theme}-${width}.png` });
            await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
            await dialog.waitFor({ state: 'detached' });
        }
    }
    // It plays, and the original downloads.
    await page.setViewportSize({ width: 390, height: 850 });
    await page.getByRole('button', { name: 'View tone.wav', exact: true }).click();
    await ready();
    await player.evaluate((a: HTMLAudioElement) => { a.muted = true; return a.play(); });
    await page.waitForFunction(() => (document.querySelector('.audio-preview-player audio') as HTMLAudioElement).currentTime > 0.3);
    const [download] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('link', { name: 'Download', exact: true }).click()]);
    assert.equal(download.suggestedFilename(), 'tone.wav');
    await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
    await dialog.waitFor({ state: 'detached' });
    // A file that can't be played says so, keeps Download, and Retry asks for a new link.
    slow = true;
    await page.getByRole('button', { name: 'View voice memo.ogg', exact: true }).click();
    await dialog.locator('.audio-preview-loading[aria-busy=true]').waitFor();
    await dialog.getByText(/Couldn't play this audio file/).waitFor();
    assert.equal(await player.evaluate(a => getComputedStyle(a.parentElement).visibility), 'hidden');
    assert.equal(await dialog.getByRole('link', { name: 'Download', exact: true }).count(), 1);
    broken = false; slow = false;
    const before = links;
    await dialog.getByRole('button', { name: 'Retry', exact: true }).click();
    await ready();
    await dialog.locator('.audio-preview-loading').waitFor({ state: 'detached' });
    assert.ok(links > before, 'Retry asked for a new preview link');
    assert.ok((await player.getAttribute('src')).includes(`test-only-${links}/`));
    assert.equal(await page.getByText('Something went wrong in the Portal').isVisible(), false, 'no Portal error');
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): audio playback in the browser's player, loading state, unplayable file with Retry for a new link, original download, 320px phone and both themes.`);
} finally { await browser.close(); }
