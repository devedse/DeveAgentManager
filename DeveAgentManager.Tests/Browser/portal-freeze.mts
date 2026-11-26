// A frozen Portal leaves a trace in the Manager's log (freeze-watch.ts): its worker reports once the page hasn't answered
// for 10 seconds while shown, with what happened before (clicks, Back, sections loading), and again once it answers,
// with how long it took and the slow frame. Short stalls and hidden tabs are not reported. Against a disposable Manager.
import assert from 'node:assert/strict';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5197';
const password = process.env.DAM_TEST_PASSWORD ?? 'preview-test-only';
const headers = { Origin: base, 'X-DAM-Request': '1' };
const browser = await playwright[engine].launch();
let session;
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    page.setDefaultTimeout(60000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login', { waitUntil: 'domcontentloaded' });
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/login')), page.locator('button[type="submit"]').click()]);

    const now = new Date().toISOString();
    const entries = [{ id: 'e0', parentId: null, kind: 'message', role: 'user', timestamp: now, content: [{ type: 'text', text: 'A private question that stays out of the log' }] }];
    const imported = await context.request.post(base + '/api/sessions/import', { headers,
        multipart: { file: { name: 'freeze.deve.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ format: 'deve-session', version: 1, title: 'Freeze test', entries })) } } });
    assert.ok(imported.ok(), await imported.text());
    session = await imported.json();

    // The Manager's log, read from outside the page (which is frozen meanwhile).
    const log = async () => (await (await context.request.get(base + '/api/logs?minimum=Warning&search=stopped%20responding&take=50')).json()) as { id: number; message: string }[];
    const before = Math.max(0, ...(await log()).map(entry => entry.id));
    const reports = async () => (await log()).filter(entry => entry.id > before);
    const waitForReports = async (count: number, seconds: number) => {
        for (const end = Date.now() + seconds * 1000; Date.now() < end; await new Promise(resolve => setTimeout(resolve, 500))) {
            const found = await reports();
            if (found.length >= count) return found;
        }
        return reports();
    };
    // Blocks the page's main thread in one of its own tasks, as an endless loop would (it ends here, so the recovery is
    // seen too). Resolves once it is over.
    const freeze = async (ms: number) => {
        await page.evaluate(ms => { setTimeout(() => { const end = Date.now() + ms; while (Date.now() < end) { /* busy */ } }, 0); }, ms);
        await new Promise(resolve => setTimeout(resolve, ms + 300));
        await page.evaluate(() => 1);
    };

    // Open the conversation from the list, then Back: what the report should tell.
    await page.goto(base + '/sessions', { waitUntil: 'domcontentloaded' });
    await page.getByText('Freeze test').first().click();
    await page.getByText('A private question').waitFor();
    await page.goBack();
    await page.getByRole('heading', { name: 'Your sessions' }).waitFor();
    await page.waitForTimeout(1500);
    assert.equal((await reports()).length, 0, 'nothing reported before');

    // A short stall is not a freeze.
    await freeze(4000);
    await page.waitForTimeout(2000);
    assert.equal((await reports()).length, 0, 'a 4-second stall is not reported');

    // A freeze: reported while it lasts, with what came before it.
    const frozen = freeze(16000);
    frozen.catch(() => { /* Seen below; a failed check closes the browser meanwhile. */ });
    const [still] = await waitForReports(1, 15);
    assert.ok(still, 'reported while frozen');
    assert.match(still.message, /^The Portal stopped responding in admin's browser: no answer for 1\d s, still frozen \(page \/sessions, open for /);
    assert.match(still.message, /Memory: \.NET \d+ MB, JavaScript \d+ MB, [\d,]+ elements on the page\./);
    assert.match(still.message, /Browser: Mozilla/);
    assert.match(still.message, new RegExp(`-\\d+\\.\\d s  click link → /sessions/${session.id}`), 'the click on the conversation');
    assert.match(still.message, /s  back or forward \/sessions\n/, 'Back');
    assert.match(still.message, /s  left SessionDetail/, 'the conversation page was left');
    assert.match(still.message, /s  loaded SessionList in \d+ ms/, 'the list loaded');
    assert.match(still.message, /s  slow frame [34]\d\d\d ms/, 'the 4-second stall was a slow frame');
    assert.doesNotMatch(still.message, /private question/, "the page's text stays out of the log");
    await frozen;

    // Answering again: reported once more, with how long it took and the slow frame that was the freeze.
    const after = await waitForReports(2, 15);
    assert.equal(after.length, 2, 'reported again once it answered');
    assert.match(after[0].message, /^The Portal stopped responding for 1\d s in admin's browser, then responded again \(page \/sessions/);
    assert.match(after[0].message, /\n\+\d+\.\d s  slow frame 1\d\d\d\d ms/, 'the frozen frame, after the freeze');

    // A hidden tab's timers run late by design: not reported.
    await page.evaluate(() => {
        Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    await page.waitForTimeout(500);
    await freeze(13000);
    await page.waitForTimeout(4000);
    assert.equal((await reports()).length, 2, 'not reported while hidden');
    await page.evaluate(() => {
        Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
        document.dispatchEvent(new Event('visibilitychange'));
    });

    assert.deepEqual(errors, []);
    console.log(`Passed (${engine}): a frozen Portal is reported to the Manager's log while frozen (page, memory, clicks, Back, sections, slow frames, no page text) and again once it answers; short stalls and hidden tabs are not.`);
} finally {
    await browser.close();
}
