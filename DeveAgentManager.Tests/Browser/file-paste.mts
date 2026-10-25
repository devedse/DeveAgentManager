const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
import assert from 'node:assert/strict';

// Files into the terminal (Ctrl+V, also a file copied in the file manager, or dropped): any file goes to the Runner over
// the terminal's socket, with its name, and each path gets its own paste so Codex recognizes image attachments.
// Text copied with a picture stays text.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000001', now = new Date().toISOString();
    const agent = { id, runnerId: id, ownerId: id, ownerName: 'Test user', access: 'Owner', runnerName: 'runner-01', runnerState: 'Ready', runnerHarnessIds: ['claude'],
        runnerToolIds: [], name: 'paste-test', harnessId: 'claude', workingDirectory: '/work/example', state: 'Running', loginOwnerId: id, loginOwnerName: 'Test user', createdAt: now, updatedAt: now };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/')) return route.continue();
        const body = path === '/api/info' ? info : path === `/api/agents/${id}` ? agent : path.endsWith('/limit/automatic') ? { enabled: false, sent: 0 }
            : path.endsWith('/limit') || path.endsWith('/session') ? null : [];
        return route.fulfill({ json: body });
    });

    // The Manager's side of the terminal socket: uploads are stored (answered with a path), everything else is typing.
    const uploads = [];
    let typed = '', answerDelay = 0, failNext = false;
    await context.routeWebSocket('**/ws/terminal?**', ws => {
        let receiving = null;
        ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/9' }));
        // Codex and the other interactive harnesses enable bracketed paste.
        ws.send(Buffer.from('\x1b[?2004h'));
        ws.onMessage(message => {
            if (typeof message === 'string') {
                const control = JSON.parse(message);
                if (control.type === 'image') receiving = { ...control, chunks: [], received: 0 };
                return;
            }
            const bytes = Buffer.from(message);
            if (receiving) {
                receiving.chunks.push(bytes);
                receiving.received += bytes.length;
                if (receiving.received === receiving.size) {
                    const done = receiving;
                    receiving = null;
                    const failed = failNext;
                    failNext = false;
                    const content = Buffer.concat(done.chunks);
                    // A large file is checked by its bytes, not kept as text.
                    if (!failed) uploads.push(content.length > 1024 * 1024
                        ? { name: done.name, size: content.length, allSame: content.every(byte => byte === content[0]), first: content[0] }
                        : { name: done.name, content: content.toString('utf8') });
                    setTimeout(() => ws.send(JSON.stringify(failed ? { type: 'image', id: done.id, error: 'Test-only storage failure' }
                        : { type: 'image', id: done.id, path: `/home/runner/.deveagentmanager/uploads/20261011-010203-abcd1234/${done.name}` })), answerDelay);
                }
                return;
            }
            typed += bytes.toString('utf8');
        });
    });

    await page.goto(base + `/agents/${id}`);
    await page.locator('.xterm').first().waitFor({ timeout: 60000 });
    await page.waitForTimeout(1500);
    // What the browser gets on Ctrl+V or a drop, built in the page (a real OS clipboard isn't available in a test).
    const deliver = (kind, files, text = '') => page.evaluate(({ kind, files, text }) => {
        const data = new DataTransfer();
        for (const file of files) data.items.add(new File([file.content], file.name, { type: file.type }));
        if (text) data.setData('text/plain', text);
        const target = document.querySelector('.xterm-helper-textarea') ?? document.querySelector('.xterm');
        const event = kind === 'paste'
            ? new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true })
            : new DragEvent('drop', { dataTransfer: data, bubbles: true, cancelable: true });
        target.dispatchEvent(event);
        return event.defaultPrevented;
    }, { kind, files, text });
    // Firefox ignores the data of a paste event made by a script (a real Ctrl+V has it): there, only drops can be tested.
    const scriptedPaste = await page.evaluate(() => {
        const data = new DataTransfer();
        data.items.add(new File(['x'], 'probe.txt'));
        let files = 0;
        const probe = event => { files = event.clipboardData?.files?.length ?? 0; event.stopImmediatePropagation(); };
        window.addEventListener('paste', probe, { capture: true, once: true });
        window.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data }));
        return files > 0;
    });
    const waitFor = async (check, what) => {
        for (let i = 0; i < 100 && !check(); i++) await page.waitForTimeout(50);
        assert.ok(check(), what);
    };

    if (scriptedPaste) {
    // A log copied in the file manager: uploaded under its own name, its path typed.
    assert.equal(await deliver('paste', [{ name: 'manager.log', type: 'text/plain', content: 'info: started\nwarn: slow start\n' }]), true);
    await waitFor(() => uploads.length === 1 && typed.includes('/uploads/20261011-010203-abcd1234/manager.log '), `log uploaded and its path typed: ${JSON.stringify({ uploads, typed })}`);
    assert.deepEqual(uploads[0], { name: 'manager.log', content: 'info: started\nwarn: slow start\n' });

    // Linux file managers put the file's local path next to it as text: still the file.
    typed = '';
    assert.equal(await deliver('paste', [{ name: 'build.zip', type: 'application/zip', content: 'PK' }], '/home/me/build.zip'), true);
    await waitFor(() => uploads.length === 2 && typed.includes('/build.zip '), 'a copied file wins over its local path');

    // A picture copied with text (a spreadsheet, a web page): the text is pasted, nothing is uploaded.
    assert.equal(await deliver('paste', [{ name: 'image.png', type: 'image/png', content: 'png' }], 'Quarter 3 revenue'), false);
    await page.waitForTimeout(500);
    assert.equal(uploads.length, 2, 'text with a picture stays text');
    }

    // Dropped files too.
    typed = '';
    const before = uploads.length;
    assert.equal(await deliver('drop', [{ name: 'notes.md', type: 'text/markdown', content: '# Notes' }]), true);
    await waitFor(() => uploads.length === before + 1 && typed.includes('/notes.md '), 'a dropped file is uploaded');
    assert.deepEqual(uploads.at(-1), { name: 'notes.md', content: '# Notes' });

    const images = [
        { name: 'first.png', type: 'image/png', content: 'first image' },
        { name: 'second.jpg', type: 'image/jpeg', content: 'second image' },
    ];
    const pastes = () => [...typed.matchAll(/\x1b\[200~([\s\S]*?)\x1b\[201~/g)].map(match => match[1]);
    const expected = files => files.map(file => `/home/runner/.deveagentmanager/uploads/20261011-010203-abcd1234/${file.name} `);
    for (const kind of scriptedPaste ? ['paste', 'drop'] : ['drop']) {
        typed = '';
        const count = uploads.length;
        assert.equal(await deliver(kind, images), true);
        await waitFor(() => uploads.length === count + images.length && pastes().length === images.length, 'each image gets its own bracketed paste');
        assert.deepEqual(pastes(), expected(images));
        assert.equal(typed, expected(images).map(path => `\x1b[200~${path}\x1b[201~`).join(''), 'no Enter or extra terminal input');
    }

    // Several files: a row each while they upload, done turns green and fades, and nothing is left behind.
    await page.locator('.terminal-uploads').waitFor({ state: 'detached', timeout: 10000 });
    answerDelay = 800;
    typed = '';
    await deliver('drop', images);
    const rows = page.locator('.terminal-upload');
    await rows.nth(1).waitFor();
    assert.equal(await rows.count(), 2, 'one row per file');
    const box = await page.locator('.terminal-uploads').boundingBox();
    const toolbar = await page.locator('.terminal-toolbar').boundingBox();
    const keys = await page.locator('.terminal-keys').boundingBox();
    assert.ok(box.y >= toolbar.y + toolbar.height && (!keys || box.y + box.height <= keys.y), 'uploads stay clear of the toolbar and the keys');
    await page.locator('.terminal-upload-done').first().waitFor();
    await page.locator('.terminal-uploads').waitFor({ state: 'detached', timeout: 10000 });
    await waitFor(() => pastes().length === images.length, 'slow uploads completed');
    assert.equal(await page.locator('.mud-snackbar').count(), 0, 'no upload message is left behind');
    answerDelay = 0;
    // A failed upload says why, and can be dismissed.
    failNext = true;
    await deliver('drop', [images[0]]);
    await page.locator('.terminal-upload-error').getByText('Test-only storage failure', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Dismiss first.png', exact: true }).click();
    await page.locator('.terminal-uploads').waitFor({ state: 'detached' });

    // A mixed batch retains ordinary file names and original order.
    typed = '';
    const mixed = [images[0], { name: 'results.log', type: 'text/plain', content: 'passed' }, images[1]];
    await deliver('drop', mixed);
    await waitFor(() => pastes().length === mixed.length, 'mixed batch completed');
    assert.deepEqual(pastes(), expected(mixed));

    // A large file (over the old 25 MB) goes in slices. What is typed meanwhile waits and arrives after it, whole,
    // instead of ending up between its bytes.
    typed = '';
    const large = uploads.length;
    await page.evaluate(() => {
        const data = new DataTransfer();
        data.items.add(new File([new Uint8Array(40 * 1024 * 1024).fill(65)], 'build.tar', { type: 'application/x-tar' }));
        (document.querySelector('.xterm-helper-textarea') ?? document.querySelector('.xterm')).dispatchEvent(new DragEvent('drop', { dataTransfer: data, bubbles: true, cancelable: true }));
    });
    await page.locator('.xterm-helper-textarea').focus();
    await page.keyboard.type('typed-meanwhile');
    for (let i = 0; i < 600 && !(uploads.length === large + 1 && pastes().length === 1); i++) await page.waitForTimeout(50);
    assert.deepEqual(uploads.at(-1), { name: 'build.tar', size: 40 * 1024 * 1024, allSame: true, first: 65 }, 'the large file arrived whole');
    assert.ok(typed.includes('typed-meanwhile'), `what was typed arrived too: ${JSON.stringify(typed.slice(0, 200))}`);
    assert.deepEqual(pastes(), [`/home/runner/.deveagentmanager/uploads/20261011-010203-abcd1234/build.tar `]);

    // The phone's attachment button uses the same path, including multiple selected images.
    await page.setViewportSize({ width: 390, height: 850 });
    typed = '';
    const chooser = page.waitForEvent('filechooser');
    await page.getByRole('button', { name: 'Send an image', exact: true }).click();
    await (await chooser).setFiles(images.map(file => ({ name: file.name, mimeType: file.type, buffer: Buffer.from(file.content) })));
    await waitFor(() => pastes().length === images.length, 'phone image picker completed');
    assert.deepEqual(pastes(), expected(images));

    console.log(`Passed (${engine}): file upload names/content, separate image paste events, upload rows (progress, done, failure, nothing left behind), mixed batch order, a 40 MB file in slices with typing held until it is sent, phone multi-image picker, no automatic submission${scriptedPaste ? ', text/image clipboard precedence' : ' (scripted clipboard unavailable; drops and picker checked)'}.`);
} finally {
    await browser.close();
}
