import assert from 'node:assert/strict';
const { chromium } = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));

// Run against an isolated preview Manager; these checks change its pet settings.
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5179';
const password = process.env.DAM_TEST_PASSWORD ?? 'test-only-pets-preview-1234';
const browser = await chromium.launch();
const errors: string[] = [];
async function login(context) {
    const p = await context.newPage();
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
try {
    const context = await browser.newContext({ viewport: { width: 1360, height: 960 } });
    const page = await login(context);
    const user = await api(context, 'me');
    await api(context, 'pets/settings', 'PUT', { enabled: true, defaultForNewUsers: false });
    await api(context, 'pets/people/' + user.id, 'PUT', { enabled: true, name: 'Snek', species: 'snek', level: 10, happiness: 80 });
    const pet = page.locator('.dam-pet-body');
    await pet.waitFor(); await page.locator('.dam-pet-art .pet-canvas').waitFor();
    await page.evaluate(() => { (window as any).petCanvas = document.querySelector('.dam-pet-art .pet-canvas'); });
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
    const menu = page.getByRole('menu', { name: 'Pet interactions' }); await menu.waitFor();
    await page.screenshot({ path: '/tmp/damagochi-desktop.png' });
    await page.getByRole('menuitem', { name: 'Go away for 5 minutes', exact: true }).click();
    await pet.waitFor({ state: 'hidden' });
    const state = await api(context, 'me/pet'); assert.equal(state.pet.enabled, true); assert.ok(state.pet.snoozedUntil);
    await api(context, 'me/pet/interact', 'POST', { action: 'wake' }); await pet.waitFor();
    // Byte uses the same host and his own art assets; progress is retained.
    await api(context, 'me/pet', 'PUT', { enabled: true, name: 'Byte', species: 'silkie', reducedMotion: true, size: 150 });
    await page.locator('.dam-pet-name', { hasText: 'Byte' }).waitFor();
    await page.waitForFunction(() => document.querySelector('.dam-pet-art .pet-canvas') && document.querySelector('.dam-pet-art .pet-canvas') !== (window as any).petCanvas);
    await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-art .illustrated-pet')?.dataset.cell !== undefined);
    assert.equal((await api(context, 'me/pet')).pet.level, 10);
    await page.screenshot({ path: '/tmp/damagochi-byte.png' });
    await pet.click({ button: 'right' }); await page.getByRole('menuitem', { name: 'Hide until I turn it on again' }).click();
    await pet.waitFor({ state: 'hidden' }); assert.equal((await api(context, 'me/pet')).pet.enabled, false);
    await api(context, 'me/pet', 'PUT', { enabled: true, name: 'Byte', species: 'silkie', reducedMotion: false, size: 140 }); await pet.waitFor();
    await api(context, 'pets/settings', 'PUT', { enabled: false, defaultForNewUsers: false }); await pet.waitFor({ state: 'hidden' });
    assert.equal((await api(context, 'me/pet')).pet.enabled, true, 'Global switch preserves the individual switch');
    await api(context, 'pets/settings', 'PUT', { enabled: true, defaultForNewUsers: false }); await pet.waitFor();
    await context.close();
    const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, reducedMotion: 'reduce' });
    const mobile = await login(phone);
    await mobile.locator('.dam-pet-art .pet-canvas').waitFor();
    assert.ok(await mobile.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    const mobilePet = mobile.locator('.dam-pet-body');
    await mobilePet.focus(); await mobile.keyboard.press('Shift+F10');
    await mobile.getByRole('menu', { name: 'Pet interactions' }).waitFor();
    const menuBox = await mobile.locator('.dam-pet-menu').boundingBox();
    assert.ok(menuBox!.x >= 0 && menuBox!.x + menuBox!.width <= 391);
    await mobile.screenshot({ path: '/tmp/damagochi-phone.png' });
    await mobile.keyboard.press('Escape'); await mobile.locator('.dam-pet-menu').waitFor({ state: 'detached' });
    const touch = await phone.newCDPSession(mobile); const touchBox = await mobilePet.boundingBox();
    await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: touchBox!.x + touchBox!.width / 2, y: touchBox!.y + touchBox!.height / 2 }] });
    await mobile.waitForTimeout(750);
    await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await mobile.getByRole('menu', { name: 'Pet interactions' }).waitFor();
    await touch.detach();
    await phone.close();
    assert.deepEqual(errors, []);
    console.log('PASS persistent canvas across both layouts, dragging, menu, snooze/wake, Byte, hide/restore, global toggle, phone and reduced motion');
} finally { await browser.close(); }
