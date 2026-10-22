const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

// Hold to confirm (HoldButton) on the Runner actions: Start or Stop, Restart and Update all on each Runner card and in
// the Runner page's header. A click only shows "Hold 3 seconds to confirm", which fades again; releasing or leaving
// early does nothing; holding 3 seconds counts down, says "Stopping…" and starts it. Mouse, keyboard and touch, the
// help on hover, a bubble that stays on a phone's screen, and light and dark.
const engine = process.env.DAM_TEST_BROWSER ?? 'chromium';
const browser = await playwright[engine].launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const id = n => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const now = new Date().toISOString();

async function signIn(context) {
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    return { page, errors };
}

async function mock(context) {
    const info = await (await context.request.get(base + '/api/info')).json();
    const me = await (await context.request.get(base + '/api/me')).json();
    // The existing-machine provider can't power machines; here it can, as Proxmox does.
    info.providers = info.providers.map(p => p.id === 'existing' ? { ...p, capabilities: { ...p.capabilities, canStartStop: true } } : p);
    const agent = (n, runner, state) => ({ id: id(100 + n), runnerId: runner, ownerId: me.id, ownerName: 'Admin', access: 'Owner', runnerName: 'runner', runnerState: 'Ready',
        runnerHarnessIds: ['claude'], runnerToolIds: [], name: `agent-${n}`, harnessId: 'claude', workingDirectory: '/work/example', state, createdAt: now, updatedAt: now, canUseRunner: true });
    const runner = (n, name, state, agents) => ({ id: id(n), name, ownerId: me.id, ownerName: 'Admin', access: 'Owner', providerId: 'existing', kind: 'Headless', state,
        machineId: String(30000 + n), host: `runner-${n}.test`, username: 'runneruser', sshPort: 22, cores: 4, memoryMb: 8192, diskGb: 64, agents, harnessIds: ['claude'], toolIds: [],
        setupVersion: info.setupVersion, createdAt: now, updatedAt: now, installedVersions: 'Claude Code 2.1.0 · node v24 · tmux 3.5a' });
    const runners = [
        runner(1, 'runner-01', 'Ready', [agent(1, id(1), 'Running'), agent(2, id(1), 'Running'), agent(3, id(1), 'Stopped')]),
        runner(2, 'runner-02', 'Stopped', []),
        runner(3, 'runner-03-with-a-longer-name', 'Ready', []),
    ];
    const posts = [];
    await context.route('**/api/**', route => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        if (request.method() === 'POST' && /^\/api\/runners\/[^/]+\/(start|stop|restart|setup)$/.test(path)) {
            posts.push(path.replace('/api/runners/', ''));
            return route.fulfill({ status: 202, body: '' });
        }
        const byId = runners.find(r => path === `/api/runners/${r.id}`);
        const body = path === '/api/info' ? info : path === '/api/runners' ? runners : byId ? byId
            : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
        return route.fulfill({ json: body });
    });
    return { runners, posts };
}

async function run(theme) {
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 }, colorScheme: theme });
    await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
    const { page, errors } = await signIn(context);
    const { posts } = await mock(context);
    const card = name => page.locator('.runner-card', { has: page.locator('.runner-card-name', { hasText: new RegExp(`^${name}$`) }) });
    const action = (scope, name) => scope.locator('.hold-button', { has: page.getByRole('button', { name, exact: true }) });
    const bubble = scope => scope.locator('.hold-bubble');
    const press = async (target, ms) => {
        const box = await target.locator('button').boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        if (ms) await page.waitForTimeout(ms);
    };

    await page.goto(base + '/runners');
    const stop = action(card('runner-01'), 'Stop');
    await stop.waitFor({ timeout: 60000 });
    assert.deepEqual(await card('runner-01').locator('.runner-card-actions button').allInnerTexts(), ['Stop', 'Restart', 'Update all']);
    assert.deepEqual(await card('runner-02').locator('.runner-card-actions button').allInnerTexts(), ['Start', 'Restart', 'Update all']);
    assert.ok(await action(card('runner-02'), 'Restart').locator('button').isDisabled(), 'Stopped: no restart');
    assert.ok(await action(card('runner-02'), 'Update all').locator('button').isDisabled(), 'Stopped: no setup');

    // Hover explains what it does and that it needs holding.
    await stop.locator('button').hover();
    const tip = page.locator('.help-tooltip.mud-popover-open');
    await tip.waitFor();
    assert.match(await tip.innerText(), /Shuts the VM down, which stops its 2 running agents\.[\s\S]*Hold for 3 seconds to confirm\./);
    await action(card('runner-01'), 'Update all').locator('button').hover();
    await page.waitForFunction(() => /Re-runs the setup script/.test(document.querySelector('.help-tooltip.mud-popover-open')?.innerText ?? ''));

    // A click: the hint, which fades again, and nothing happens.
    await press(stop);
    await page.mouse.up();
    await bubble(stop).waitFor();
    assert.match(await bubble(stop).innerText(), /Hold 3 seconds to confirm/);
    assert.equal(await tip.count(), 0, 'The help is out of the way of the hint');
    await bubble(stop).waitFor({ state: 'detached', timeout: 4000 });
    assert.deepEqual(posts, []);

    // Released halfway: counted down to 2, then gone without stopping.
    await press(stop, 1400);
    assert.match(await bubble(stop).innerText(), /^2\s*Hold 3 seconds to confirm$/);
    assert.ok(await stop.locator('.hold-button-holding').count() === 1, 'The button fills while held');
    await page.screenshot({ path: `/tmp/dam-hold-${theme}.png`, clip: await (async () => { const b = await card('runner-01').boundingBox(); return { x: b.x - 20, y: b.y + b.height - 140, width: b.width + 40, height: 160 }; })() });
    await page.mouse.up();
    await bubble(stop).waitFor({ state: 'detached', timeout: 4000 });
    assert.deepEqual(posts, []);

    // Moving off the button cancels too.
    await press(stop, 800);
    await page.mouse.move(5, 5);
    await page.waitForTimeout(3000);
    await page.mouse.up();
    assert.deepEqual(posts, []);

    // Held for 3 seconds: "Restarting…", and it starts; the page doesn't follow the card's link.
    const restart = action(card('runner-01'), 'Restart');
    await press(restart, 3300);
    assert.match(await bubble(restart).innerText(), /Restarting…/);
    await page.mouse.up();
    assert.deepEqual(posts, [`${id(1)}/restart`]);
    assert.equal(new URL(page.url()).pathname, '/runners');
    await bubble(restart).waitFor({ state: 'detached', timeout: 5000 });

    // The keyboard: hold Space.
    const update = action(card('runner-03-with-a-longer-name'), 'Update all');
    await update.locator('button').focus();
    await page.keyboard.down(' ');
    await page.waitForTimeout(3300);
    await page.keyboard.up(' ');
    assert.deepEqual(posts.at(-1), `${id(3)}/setup`);

    // The rest of the card still opens the Runner (its name's link covers it).
    const facts = await card('runner-02').locator('.runner-card-facts').boundingBox();
    await page.mouse.click(facts.x + facts.width / 2, facts.y + facts.height / 2);
    await page.waitForURL(base + `/runners/${id(2)}`);

    // The Runner page: the same buttons and explanations in its header, and Re-run setup says it is the same.
    await page.goto(base + `/runners/${id(1)}`);
    const header = page.locator('.page-header-actions');
    await action(header, 'Stop').waitFor({ timeout: 60000 });
    assert.deepEqual((await header.locator('.hold-button button').allInnerTexts()), ['Stop', 'Restart', 'Update all']);
    await page.getByText('Same as Update all.').waitFor();
    await action(header, 'Restart').locator('button').hover();
    await page.waitForFunction(() => /Shuts the VM down and starts it again, which stops its 2 running agents/.test(document.querySelector('.help-tooltip.mud-popover-open')?.innerText ?? ''));
    await press(action(header, 'Stop'), 3300);
    await page.mouse.up();
    assert.deepEqual(posts.at(-1), `${id(1)}/stop`);
    assert.equal(await page.locator('.mud-dialog').count(), 0, 'No confirmation dialog');
    await page.locator('.mud-tab.mud-tab-active', { hasText: 'Setup log' }).waitFor();
    assert.deepEqual(errors, []);
    await context.close();
}

async function phone(theme) {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, colorScheme: theme, hasTouch: true, isMobile: true });
    await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
    const { page, errors } = await signIn(context);
    const { posts } = await mock(context);
    await page.goto(base + '/runners');
    const action = name => page.locator('.runner-card').first().locator('.hold-button', { has: page.getByRole('button', { name, exact: true }) });
    const update = action('Update all');
    await update.waitFor({ timeout: 60000 });
    await update.scrollIntoViewIfNeeded();
    // A finger held on the button, at the right edge of the screen: the bubble stays on screen.
    const box = await update.locator('button').boundingBox();
    const cdp = await context.newCDPSession(page);
    const point = [{ x: box.x + box.width / 2, y: box.y + box.height / 2 }];
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: point });
    await update.locator('.hold-bubble').waitFor();
    await page.waitForTimeout(600);
    const rect = await update.locator('.hold-bubble').boundingBox();
    assert.ok(rect.x >= 0 && rect.x + rect.width <= 390, `The bubble is on screen (${rect.x}–${rect.x + rect.width})`);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Phone: no horizontal scroll');
    await page.screenshot({ path: `/tmp/dam-hold-phone-${theme}.png` });
    await page.waitForTimeout(2800);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    assert.deepEqual(posts, [`${id(1)}/setup`]);
    assert.match(await update.locator('.hold-bubble').innerText(), /Updating…/);
    // A tap only shows the hint.
    const stop = action('Stop');
    await stop.locator('button').tap();
    await stop.locator('.hold-bubble').waitFor();
    await stop.locator('.hold-bubble').waitFor({ state: 'detached', timeout: 4000 });
    assert.deepEqual(posts, [`${id(1)}/setup`]);
    assert.deepEqual(errors, []);
    await context.close();
}

try {
    for (const theme of ['light', 'dark']) {
        await run(theme);
        await phone(theme);
    }
    console.log(`Passed (${engine}): Runner Start/Stop, Restart and Update all on the cards and the Runner page need a 3-second hold; a click or an early release only shows the hint, which fades; mouse, keyboard and touch; help on hover; on screen on a phone; light and dark.`);
} finally {
    await browser.close();
}
