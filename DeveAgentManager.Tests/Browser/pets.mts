import assert from 'node:assert/strict';
const { chromium } = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));

// Run against an isolated preview Manager; these checks change its pet settings.
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5179';
const password = process.env.DAM_TEST_PASSWORD ?? 'test-only-pets-preview-1234';
const browser = await chromium.launch();
const errors: string[] = [];
async function login(context) {
    const p = await context.newPage();
    p.setDefaultTimeout(20000);
    p.on('pageerror', e => errors.push(e.message));
    p.on('console', message => {
        if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text());
    });
    await p.goto(base + '/login');
    await p.locator('input[name="username"]').fill('admin');
    await p.locator('input[name="password"]').fill(password);
    await Promise.all([p.waitForURL(base + '/'), p.locator('button[type="submit"]').click()]);
    await p.locator('.app-bar').waitFor({ timeout: 60000 });
    return p;
}
async function api(context, path: string, method = 'GET', data?: unknown) {
    const response = await context.request.fetch(base + '/api/' + path, { method, data, headers: { 'X-DAM-Request': '1' } });
    assert.ok(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`);
    return response.status() === 204 ? null : await response.json();
}
async function farewell(page: import('playwright').Page, activate: () => Promise<unknown>, hasFace = true, otherScreens: import('playwright').Page[] = []) {
    const checks = [page, ...otherScreens].map(async screen => {
        if (hasFace) {
            await screen.locator('.dam-pet-host[data-departing="true"][data-motion="sad"]').waitFor();
            await screen.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-art .illustrated-pet')?.dataset.currentMotion === 'sleep');
        }
        await screen.waitForFunction(() => Number.parseFloat(document.querySelector<HTMLElement>('.dam-pet-art')?.style.opacity || '1') < .8);
        await screen.locator('.dam-pet-body').waitFor({ state: 'hidden' });
    });
    await activate();
    await Promise.all(checks);
}
try {
    const context = await browser.newContext({ viewport: { width: 1360, height: 960 } });
    const page = await login(context);
    const user = await api(context, 'me');
    await api(context, 'pets/settings', 'PUT', { enabled: true, defaultForNewUsers: false });
    await api(context, 'pets/people/' + user.id, 'PUT', { enabled: true, name: '', species: '', level: 1, happiness: 70, lifetimeTokens: 1_000_000 });
    await page.locator('.dam-pet-name', { hasText: 'Choose Damagochi & name' }).waitFor();
    assert.equal((await api(context, 'me/pet')).pet.needsAdoption, true);
    await page.locator('.dam-pet-body').click();
    await page.getByRole('button', { name: 'Choose Damagochi and hatch egg' }).waitFor();
    assert.equal(await page.getByLabel('Name your Damagochi', { exact: true }).inputValue(), '');
    assert.equal(await page.getByRole('button', { name: 'Choose Damagochi and hatch egg' }).isDisabled(), true);
    await page.getByLabel('Choose a Damagochi', { exact: true }).click();
    await page.getByRole('option', { name: 'Galaxy snake', exact: true }).click();
    await page.getByLabel('Name your Damagochi', { exact: true }).fill('Snek');
    await page.getByRole('button', { name: 'Choose Damagochi and hatch egg' }).click();
    await page.locator('.dam-pet-name', { hasText: 'Snek' }).waitFor();
    assert.equal((await api(context, 'me/pet')).pet.experience, 0);
    await api(context, 'pets/people/' + user.id, 'PUT', { enabled: true, name: 'Snek', species: 'snek', level: 1, happiness: 80, experience: 64_203_424, lifetimeTokens: 64_203_424 });
    const recalculated = (await api(context, 'me/pet')).pet;
    assert.equal(recalculated.level, 2); assert.equal(recalculated.stage, 1);
    assert.equal(recalculated.experience, 64_203_424); assert.equal(recalculated.lifetimeTokens, 64_203_424);
    const pet = page.locator('.dam-pet-body');
    await pet.waitFor(); await page.locator('.dam-pet-art .pet-canvas').waitFor();
    await page.evaluate(() => { (window as any).petCanvas = document.querySelector('.dam-pet-art .pet-canvas'); });
    await page.getByRole('link', { name: 'Agents', exact: true }).first().click();
    await page.getByRole('link', { name: 'My settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Damagochi', exact: true }).click();
    await page.getByText('Your Damagochi', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => (window as any).petCanvas === document.querySelector('.dam-pet-art .pet-canvas')), true, 'Settings navigation keeps the canvas');
    // Switch to a fullscreen terminal layout through client navigation, without needing a real Runner.
    await page.evaluate(() => { const a = document.createElement('a'); a.href = '/agents/00000000-0000-0000-0000-000000000099'; a.id = 'pet-test-terminal'; document.body.append(a); a.click(); a.remove(); });
    await page.locator('.terminal-layout').waitFor();
    assert.equal(await page.evaluate(() => (window as any).petCanvas === document.querySelector('.dam-pet-art .pet-canvas')), true, 'Terminal layout keeps the same renderer');
    await page.getByRole('link', { name: 'Back to agents', exact: true }).first().click();
    await page.locator('.app-main').waitFor();
    assert.equal(await page.evaluate(() => (window as any).petCanvas === document.querySelector('.dam-pet-art .pet-canvas')), true);
    // Dragging updates position without treating the drag as an affection click.
    const before = await page.locator('.dam-pet-host').boundingBox();
    const box = await pet.boundingBox();
    await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
    await page.mouse.down(); await page.mouse.move(650, 350, { steps: 8 }); await page.mouse.up();
    const after = await page.locator('.dam-pet-host').boundingBox();
    assert.ok(Math.abs(after!.x - before!.x) > 100);
    await pet.click({ button: 'right' });
    const menu = page.getByRole('menu', { name: 'Damagochi interactions' }); await menu.waitFor();
    await menu.getByRole('progressbar', { name: 'Damagochi experience' }).waitFor();
    assert.equal(await menu.getByRole('menuitem', { name: /Kill/ }).count(), 0, 'Reset is only available in settings');
    await menu.getByText('14,203,424 / 100,000,000 XP to level 3', { exact: true }).waitFor();
    await menu.getByRole('button', { name: 'Help about Damagochi XP and lifetime tokens' }).click();
    await page.getByText('This Damagochi has 64,203,424 lifetime tokens.', { exact: false }).waitFor();
    await menu.getByRole('button', { name: 'Help about Damagochi XP and lifetime tokens' }).click();
    await menu.getByText('64,203,424 Damagochi lifetime tokens', { exact: true }).waitFor();
    const account = menu.getByRole('region', { name: 'Account usage', exact: true });
    await account.getByText('64,203,424 account tokens', { exact: true }).waitFor();
    await account.getByRole('progressbar', { name: 'Account token milestone' }).waitFor();
    await page.screenshot({ path: '/tmp/damagochi-desktop.png' });
    // A separate browser window receives only live updates, never the initiating click.
    const otherContext = await browser.newContext({ viewport: { width: 1200, height: 900 } });
    const otherScreen = await login(otherContext);
    const otherPet = otherScreen.locator('.dam-pet-body');
    await otherPet.waitFor(); await otherScreen.locator('.dam-pet-art .pet-canvas').waitFor();
    assert.equal(await otherScreen.evaluate(() => document.hidden), false);
    for (const minutes of [5, 15, 60]) {
        if (minutes !== 5) await pet.click({ button: 'right' });
        if (minutes === 5) {
            // Hold the acknowledgement so live updates can render the saved reaction before depart() starts.
            await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-host')?.dataset.motion === 'idle');
            await page.route('**/api/me/pet/snooze', async route => {
                const response = await route.fetch();
                await new Promise(resolve => setTimeout(resolve, 800));
                await route.fulfill({ response });
            });
            await page.evaluate(() => {
                const host = document.querySelector<HTMLElement>('.dam-pet-host')!;
                const motions: string[] = [];
                const observer = new MutationObserver(() => motions.push(host.dataset.motion ?? ''));
                observer.observe(host, { attributes: true, attributeFilter: ['data-motion'] });
                (window as any).farewellTrace = { motions, observer };
            });
        }
        const requestedAt = Date.now();
        await farewell(page, () => page.getByRole('menuitem', { name: `Go away for ${minutes} minutes`, exact: true }).click(), true, [otherScreen]);
        if (minutes === 5) {
            const motions: string[] = await page.evaluate(() => {
                const trace = (window as any).farewellTrace; trace.observer.disconnect(); return trace.motions;
            });
            assert.ok(!motions.includes('happy'), 'Snoozing must not smile before the cry/sleep/fade sequence');
            await page.unroute('**/api/me/pet/snooze');
            await otherScreen.reload();
            await otherScreen.locator(`.dam-pet-host[data-pet-user="${user.id}"]`).waitFor({ state: 'attached' });
            assert.equal(await otherPet.isVisible(), false, 'Loading an already snoozed Damagochi does not replay the farewell');
            assert.equal(await otherScreen.locator('.dam-pet-host[data-departing]').count(), 0);
        }
        const state = (await api(context, 'me/pet')).pet;
        assert.equal(state.enabled, true);
        assert.ok(Math.abs(Date.parse(state.snoozedUntil) - requestedAt - minutes * 60_000) < 10_000);
        assert.equal(state.experience, recalculated.experience); assert.equal(state.generation, recalculated.generation);
        await api(context, 'me/pet/interact', 'POST', { action: 'wake' }); await pet.waitFor(); await otherPet.waitFor();
    }
    // An explicit wake from another tab cancels the farewell rather than hiding the awake companion.
    await pet.click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Go away for 5 minutes', exact: true }).click();
    await page.locator('.dam-pet-host[data-departing="true"]').waitFor();
    await otherScreen.locator('.dam-pet-host[data-departing="true"]').waitFor();
    await api(context, 'me/pet/interact', 'POST', { action: 'wake' });
    await page.waitForFunction(() => !document.querySelector('.dam-pet-host[data-departing]'));
    await otherScreen.waitForFunction(() => !document.querySelector('.dam-pet-host[data-departing]'));
    await pet.waitFor(); await otherPet.waitFor();
    // A rejected request leaves the Damagochi visible and offers the original menu with an error.
    await page.route('**/api/me/pet/snooze', route => route.fulfill({ status: 503, body: 'Preview test: snooze unavailable' }));
    await pet.click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Go away for 5 minutes', exact: true }).click();
    await menu.getByRole('alert').waitFor(); await pet.waitFor();
    assert.equal(await page.locator('.dam-pet-host[data-departing]').count(), 0);
    assert.equal(await otherScreen.locator('.dam-pet-host[data-departing]').count(), 0); await otherPet.waitFor();
    assert.equal((await api(context, 'me/pet')).pet.snoozedUntil, null);
    await page.unroute('**/api/me/pet/snooze');
    await menu.getByRole('menuitem', { name: 'Close', exact: true }).click();
    // Dropping on the hide target uses the same five-minute farewell.
    await farewell(page, async () => {
        const bounds = (await pet.boundingBox())!;
        await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2); await page.mouse.down();
        await page.mouse.move(bounds.x + bounds.width / 2 + 20, bounds.y + bounds.height / 2, { steps: 3 });
        const target = (await page.locator('.dam-pet-hide-target').boundingBox())!;
        await page.mouse.move(target.x + target.width / 2, target.y + target.height / 2, { steps: 8 }); await page.mouse.up();
    }, true, [otherScreen]);
    assert.ok((await api(context, 'me/pet')).pet.snoozedUntil);
    await api(context, 'me/pet/interact', 'POST', { action: 'wake' }); await pet.waitFor(); await otherPet.waitFor();
    // Byte uses the same host and his own art assets; progress is retained.
    await api(context, 'pets/people/' + user.id, 'PUT', { enabled: true, name: 'Byte', species: 'silkie', level: 10, happiness: 80, lifetimeTokens: 7_000_000 });
    let current = (await api(context, 'me/pet')).pet;
    await api(context, 'me/pet', 'PUT', { ...current, reducedMotion: true, size: 150 });
    await page.locator('.dam-pet-name', { hasText: 'Byte' }).waitFor();
    await page.waitForFunction(() => document.querySelector('.dam-pet-art .pet-canvas') && document.querySelector('.dam-pet-art .pet-canvas') !== (window as any).petCanvas);
    await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-art .illustrated-pet')?.dataset.cell !== undefined);
    await otherScreen.locator('.dam-pet-name', { hasText: 'Byte' }).waitFor();
    await otherScreen.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-art .illustrated-pet')?.dataset.cell !== undefined);
    assert.equal((await api(context, 'me/pet')).pet.level, 10);
    await page.screenshot({ path: '/tmp/damagochi-byte.png' });
    await pet.click({ button: 'right' });
    await farewell(page, () => page.getByRole('menuitem', { name: 'Hide until I turn it on again' }).click(), true, [otherScreen]);
    assert.equal((await api(context, 'me/pet')).pet.enabled, false);
    current = (await api(context, 'me/pet')).pet;
    await api(context, 'me/pet', 'PUT', { ...current, enabled: true, reducedMotion: false, size: 140 }); await pet.waitFor(); await otherPet.waitFor();
    await api(context, 'pets/settings', 'PUT', { enabled: false, defaultForNewUsers: false }); await pet.waitFor({ state: 'hidden' });
    await otherPet.waitFor({ state: 'hidden' });
    assert.equal(await otherScreen.locator('.dam-pet-host[data-departing]').count(), 0, 'Global disabling still takes effect immediately');
    assert.equal((await api(context, 'me/pet')).pet.enabled, true, 'Global switch preserves the individual switch');
    await api(context, 'pets/settings', 'PUT', { enabled: true, defaultForNewUsers: false }); await pet.waitFor();
    await pet.click({ button: 'right' });
    assert.equal(await menu.getByRole('menuitem', { name: /Kill/ }).count(), 0);
    await menu.getByRole('menuitem', { name: 'Damagochi settings', exact: true }).click();
    const kill = page.getByRole('button', { name: 'Kill Damagochi and create new one…', exact: true });
    console.log("Checking crying preview and confirmation");
    await kill.hover();
    await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-host')?.dataset.motion === 'sad');
    await page.screenshot({ path: '/tmp/damagochi-crying.png' });
    await page.mouse.move(5, 5);
    await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-host')?.dataset.motion !== 'sad');
    console.log("Opening retirement confirmation");
    await kill.click();
    const dialog = page.getByRole('dialog'); await dialog.waitFor();
    await dialog.getByRole('button', { name: 'Keep Damagochi', exact: true }).click();
    assert.equal((await api(context, 'me/pet')).pet.species, 'silkie');
    console.log("Opening retirement confirmation");
    await kill.click();
    await dialog.getByText(/account tokens are kept/).waitFor();
    await dialog.getByRole('button', { name: 'Kill Damagochi', exact: true }).click();
    console.log("Checking sleep/fade sequence");
    await page.locator('.dam-pet-host[data-retiring="true"]').waitFor();
    await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-art .illustrated-pet')?.dataset.currentMotion === 'sleep');
    await page.waitForFunction(() => Number.parseFloat(document.querySelector<HTMLElement>('.dam-pet-art')?.style.opacity || '1') < .8);
    await page.locator('.dam-pet-name', { hasText: 'Choose Damagochi & name' }).waitFor();
    await page.locator('.dam-pet-art .egg-svg').waitFor();
    current = (await api(context, 'me/pet')).pet;
    assert.equal(current.experience, 0); assert.equal(current.lifetimeTokens, 7_000_000); assert.equal(current.name, '');
    await page.getByRole('region', { name: 'Account usage', exact: true }).getByText('7,000,000 account tokens', { exact: true }).waitFor();
    await pet.click({ button: 'right' });
    await account.getByText('7,000,000 account tokens', { exact: true }).waitFor();
    assert.equal(await menu.getByRole('progressbar', { name: 'Damagochi experience' }).count(), 0);
    await page.screenshot({ path: '/tmp/damagochi-new-egg.png' });
    await otherScreen.locator('.dam-pet-art .egg-svg').waitFor();
    await farewell(page, () => menu.getByRole('menuitem', { name: 'Go away for 15 minutes', exact: true }).click(), false, [otherScreen]);
    await api(context, 'me/pet/interact', 'POST', { action: 'wake' }); await pet.waitFor(); await otherPet.waitFor();
    await api(context, 'me/pet/adopt', 'POST', { name: 'Byte', species: 'silkie', generation: current.generation });
    await page.locator('.dam-pet-name', { hasText: 'Byte' }).waitFor();
    assert.equal((await api(context, 'me/pet')).pet.level, 1);
    await page.getByText('0 Damagochi lifetime tokens', { exact: true }).waitFor();
    await page.getByRole('region', { name: 'Account usage', exact: true }).getByText('7,000,000 account tokens', { exact: true }).waitFor();
    // Autonomous actions eventually interrupt idle, without changing server happiness or token totals.
    await page.waitForFunction(() => ['peck', 'walk', 'wave', 'hop'].includes(document.querySelector<HTMLElement>('.dam-pet-host')?.dataset.motion ?? ''), undefined, { timeout: 35000 });
    await api(context, 'pets/settings', 'PUT', { enabled: false, defaultForNewUsers: false });
    await pet.waitFor({ state: 'hidden' });
    await page.getByRole('region', { name: 'Account usage', exact: true }).getByText('7,000,000 account tokens', { exact: true }).waitFor();
    await api(context, 'pets/settings', 'PUT', { enabled: true, defaultForNewUsers: false });
    await context.close();
    const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
    const mobile = await login(phone);
    await mobile.locator('.dam-pet-art .pet-canvas').waitFor();
    assert.ok(await mobile.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    const mobilePet = mobile.locator('.dam-pet-body');
    await mobilePet.focus(); await mobile.keyboard.press('Shift+F10');
    await mobile.getByRole('menu', { name: 'Damagochi interactions' }).waitFor();
    assert.equal(await mobile.getByRole('menuitem', { name: /Kill/ }).count(), 0);
    const menuBox = await mobile.locator('.dam-pet-menu').boundingBox();
    assert.ok(menuBox!.x >= 0 && menuBox!.x + menuBox!.width <= 391);
    await mobile.screenshot({ path: '/tmp/damagochi-phone.png' });
    await mobile.keyboard.press('Escape'); await mobile.locator('.dam-pet-menu').waitFor({ state: 'detached' });
    const touch = await phone.newCDPSession(mobile); const touchBox = await mobilePet.boundingBox();
    await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: touchBox!.x + touchBox!.width / 2, y: touchBox!.y + touchBox!.height / 2 }] });
    await mobile.waitForTimeout(750);
    await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await mobile.getByRole('menu', { name: 'Damagochi interactions' }).waitFor();
    await touch.detach();
    await otherPet.waitFor();
    await farewell(mobile, () => mobile.getByRole('menuitem', { name: 'Go away for 60 minutes', exact: true }).click(), true, [otherScreen]);
    assert.equal((await api(phone, 'me/pet')).pet.enabled, true);
    await api(phone, 'me/pet/interact', 'POST', { action: 'wake' }); await mobilePet.waitFor();
    await otherPet.waitFor(); await otherContext.close();
    await phone.close();
    assert.deepEqual(errors, []);
    console.log('PASS adoption, XP, persistent canvas, idle, reset/account usage, synchronized go-away/drag/hide farewells, wake cancellation, failed snooze, no replay on load, egg fade and phone/reduced motion');
} finally { await browser.close(); }
