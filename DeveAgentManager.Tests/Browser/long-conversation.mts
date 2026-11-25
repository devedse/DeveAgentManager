// A long conversation whose agent keeps syncing, against a disposable Manager. The session page used to download and
// draw the whole conversation again for every change, overlapping and continuing after Back, until the browser hung and
// its .NET runtime crashed. Now it opens at the latest messages (earlier ones on request, the view kept in place),
// loads one copy at a time, pauses between downloads of a long conversation, and stops when the page is left.
import assert from 'node:assert/strict';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5197';
const password = process.env.DAM_TEST_PASSWORD ?? 'preview-test-only';
const count = 6000;
const headers = { Origin: base, 'X-DAM-Request': '1' };
const browser = await playwright[engine].launch();
const errors = [];
let session;
let context;
try {
    context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
    const page = await context.newPage();
    page.setDefaultTimeout(120000);
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login', { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/login')), page.locator('button[type="submit"]').click()]);

    // A conversation of 6,000 messages: questions, answers with Markdown and a tool call, and their results.
    const now = new Date().toISOString();
    const text = 'Some **Markdown** with `code` and a [link](https://example.com). '.repeat(8);
    const entries = Array.from({ length: count }, (_, i) => {
        const entry = { id: 'e' + i, parentId: i ? 'e' + (i - 1) : null, kind: 'message', timestamp: now };
        return i % 3 === 0 ? { ...entry, role: 'user', content: [{ type: 'text', text: `Question ${i}: ${text}` }] }
            : i % 3 === 1 ? { ...entry, role: 'assistant', content: [{ type: 'text', text: `## Answer ${i}\n\n${text}\n\n- one\n- two` }, { type: 'toolCall', id: 't' + i, name: 'Bash', arguments: { command: 'ls ' + i } }] }
            : { ...entry, role: 'tool', content: [{ type: 'toolResult', toolCallId: 't' + (i - 1), toolName: 'Bash', content: [{ type: 'text', text: 'output\n'.repeat(20) }] }] };
    });
    const imported = await context.request.post(base + '/api/sessions/import', { headers,
        multipart: { file: { name: 'long.deve.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ format: 'deve-session', version: 1, title: 'Long conversation', entries })) } } });
    assert.ok(imported.ok(), await imported.text());
    session = await imported.json();

    // Every read of the conversation's details looks like a newer sync, as for an agent that keeps working; the
    // downloads of the whole conversation are counted.
    let inFlight = 0, most = 0, downloads = 0, left = false, afterLeaving = 0;
    await context.route(`**/api/sessions/${session.id}**`, async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === `/api/sessions/${session.id}`) {
            const response = await route.fetch();
            return route.fulfill({ response, json: { ...(await response.json()), lastSyncedAt: new Date().toISOString() } });
        }
        if (path !== `/api/sessions/${session.id}/content`) return route.continue();
        downloads++; inFlight++; most = Math.max(most, inFlight);
        if (left) afterLeaving++;
        try { await route.fulfill({ response: await route.fetch() }); } finally { inFlight--; }
    });

    await page.goto(base + '/sessions', { waitUntil: 'domcontentloaded' });
    await page.getByText('Long conversation').first().click();
    await page.waitForURL(`**/sessions/${session.id}`);

    // It opens at the latest 200 messages, scrolled to the end.
    await page.getByText(`Question ${count - 3}:`).waitFor();
    const earlier = page.locator('.conv-earlier');
    assert.equal((await earlier.innerText()).split('\n')[0].trim(), '3,800 earlier messages', 'counted as on the page: questions and answers');
    assert.equal(await page.getByText(`Question ${count - 303}:`).count(), 0, 'older messages are not drawn');
    assert.ok(await page.evaluate(() => scrollY + innerHeight >= document.documentElement.scrollHeight - 4), 'opens at the end');
    const opened = await page.evaluate(() => document.getElementsByTagName('*').length);

    // Earlier messages show above, and what was on screen stays where it was.
    const firstShown = page.getByText(`Question ${count - 300}:`);
    await firstShown.scrollIntoViewIfNeeded();
    const before = (await firstShown.boundingBox()).y;
    await earlier.getByRole('button', { name: 'Show 200', exact: true }).click();
    await page.getByText(`Question ${count - 600}:`).waitFor({ state: 'attached' });
    await page.waitForTimeout(300);
    assert.ok(Math.abs((await firstShown.boundingBox()).y - before) < 4, 'the view stays in place');
    assert.match(await earlier.innerText(), /^3,600 earlier messages/);

    // While the agent keeps syncing (a change every two seconds), one download at a time, with pauses, and the page
    // stays responsive.
    const sync = setInterval(() => context.request.put(`${base}/api/sessions/${session.id}/title`, { headers, data: { title: 'Long conversation ' + Date.now() } }).catch(() => {}), 2000);
    let changes = 0;
    const changing = setInterval(() => changes++, 2000);
    let worst = 0;
    for (const end = Date.now() + 30000; Date.now() < end;) {
        const start = Date.now();
        await page.evaluate(() => 1);
        worst = Math.max(worst, Date.now() - start);
        await page.waitForTimeout(250);
    }
    clearInterval(changing);
    assert.equal(most, 1, 'never two copies of the conversation at once');
    assert.ok(downloads < changes / 2, `downloads paused: ${downloads} downloads for ${changes} changes`);
    assert.ok(worst < 3000, `the page stays responsive (longest wait ${worst} ms)`);
    assert.ok(await page.evaluate(() => document.getElementsByTagName('*').length) < opened * 3, 'the drawn messages stay bounded');

    // Back leaves at once, and nothing more is downloaded.
    const back = Date.now();
    left = true;
    await page.goBack();
    await page.getByRole('heading', { name: 'Your sessions' }).waitFor();
    const took = Date.now() - back;
    await page.waitForTimeout(5000);
    clearInterval(sync);
    assert.ok(took < 3000, `Back took ${took} ms`);
    assert.equal(afterLeaving, 0, 'no downloads after leaving the page');

    // Show all draws every message; the bar fits a phone in both themes.
    await page.setViewportSize({ width: 390, height: 844 });
    for (const theme of ['light', 'dark']) {
        await page.evaluate(value => localStorage.setItem('dam-theme', value), theme);
        await page.goto(`${base}/sessions/${session.id}`, { waitUntil: 'domcontentloaded' });
        await page.getByText(`Question ${count - 3}:`).waitFor();
        await earlier.scrollIntoViewIfNeeded();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'no horizontal scroll on a phone');
        if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: `${process.env.DAM_SCREENSHOTS}/long-conversation-${engine}-${theme}.png` });
    }
    await earlier.getByRole('button', { name: 'Show all', exact: true }).click();
    await page.getByText('Question 0:').waitFor({ state: 'attached' });
    assert.equal(await earlier.count(), 0);
    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): a ${count}-message conversation opens at its latest 200, shows earlier ones in place, loads one copy at a time with pauses while syncing, stays responsive, leaves at once on Back and fits a phone.`);
} catch (error) {
    for (const page of context?.pages() ?? []) {
        if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: process.env.DAM_SCREENSHOTS + '/failure-long-conversation-' + engine + '.png' }).catch(() => {});
    }
    throw error;
} finally {
    if (session) await context.request.delete(`${base}/api/sessions/${session.id}`, { headers }).catch(() => {});
    await browser.close();
}
