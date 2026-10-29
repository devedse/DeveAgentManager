import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createServer } from 'node:http';
import { chromium, type Page } from 'playwright';

// Exercise the shipped host and renderers without changing a Manager account.
const webRoot = path.resolve('DeveAgentManager/wwwroot');
const fixture = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"></head><body>
<div class="dam-pet-host"><div class="dam-pet-body" tabindex="0"><div class="dam-pet-art"></div><span class="dam-pet-name">Throw preview</span></div><div class="dam-pet-hide-target">Hide</div></div>
<script type="module">
import * as host from '/js/pet-host.js';
const root=document.querySelector('.dam-pet-host'), mount=root.querySelector('.dam-pet-art'),calls=[];
let snapshot;
window.petTest={calls,async reset(species='snek',stage=3,reducedMotion=false){
host.dispose(root);calls.length=0;localStorage.setItem('dam-pet-position:throw-preview',JSON.stringify({x:.3,y:.6}));
snapshot={userId:'throw-preview',name:'Preview',species,stage,stageProgress:.5,happiness:80,size:140,reducedMotion,revision:1,reaction:'idle',reactionAt:null,generation:1,needsAdoption:false,enabled:true,snoozedUntil:null};
root.hidden=false;host.initialize(root,mount,{invokeMethodAsync:async method=>{calls.push(method)}});
await host.update(root,snapshot,species==='snek'?'snake-art':'silkie-art',true);
},async hide(){await host.update(root,snapshot,snapshot.species==='snek'?'snake-art':'silkie-art',false)},depart(){return host.depart(root,snapshot)}};
await window.petTest.reset();
</script></body></html>`;
const server = createServer(async (request, response) => {
    try {
        const pathname = new URL(request.url!, 'http://127.0.0.1').pathname;
        if (pathname === '/') { response.setHeader('Content-Type', 'text/html'); response.end(fixture); return; }
        const file = path.resolve(webRoot, '.' + decodeURIComponent(pathname));
        if (!file.startsWith(webRoot + path.sep)) { response.writeHead(404).end(); return; }
        const type = file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : file.endsWith('.json') ? 'application/json' : 'image/webp';
        response.setHeader('Content-Type', type); response.end(await fs.readFile(file));
    } catch { response.writeHead(404).end(); }
});
await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
const base = 'http://127.0.0.1:' + (server.address() as { port: number }).port;
const browser = await chromium.launch();
const errors: string[] = [];
const position = (page: Page) => page.locator('.dam-pet-host').boundingBox();
async function reset(page: Page, species = 'snek', stage = 3, reducedMotion = false) {
    await page.evaluate(async args => (window as any).petTest.reset(...args), [species, stage, reducedMotion]);
    await page.waitForFunction(() => document.querySelector('.dam-pet-art .illustrated-pet')?.getAttribute('data-current-motion') === 'idle');
}
async function drag(page: Page, dx: number, dy: number, pause = 0, stepDelay = 18, selector = '.dam-pet-body') {
    const body = await page.locator(selector).boundingBox();
    const x = body!.x + body!.width / 2, y = body!.y + 60;
    // Replay device sample times so browser/transport delays cannot change the tested gesture speed.
    const input = await page.context().newCDPSession(page), timestamp = Date.now() / 1000;
    try {
        await input.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, timestamp });
        await input.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount: 1, timestamp });
        for (let i = 1; i <= 4; i++) {
            await page.waitForTimeout(stepDelay);
            await input.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x + dx * i / 4, y: y + dy * i / 4,
                button: 'left', buttons: 1, timestamp: timestamp + i * stepDelay / 1000 });
        }
        if (pause) await page.waitForTimeout(pause);
        await input.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x + dx, y: y + dy, button: 'left', buttons: 0,
            clickCount: 1, timestamp: timestamp + (4 * stepDelay + pause + 2) / 1000 });
    } finally { await input.detach(); }
}
async function scaredDrag(page: Page, species: string) {
    const body = await page.locator('.dam-pet-body').boundingBox();
    await page.mouse.move(body!.x + 60, body!.y + 60); await page.mouse.down();
    await page.mouse.move(body!.x + 80, body!.y + 60);
    await page.locator('.dam-pet-host[data-motion="drag"]').waitFor();
    const face = species === 'snek' ? '[data-eyes="shocked"]' : '[data-cell="5"]';
    await page.locator('.dam-pet-art ' + face).waitFor();
    await page.waitForTimeout(180);
    assert.equal(await page.locator('.dam-pet-host').getAttribute('data-motion'), 'drag', 'the scared pose stays while held');
    await page.mouse.up();
    await page.waitForFunction(() => document.querySelector<HTMLElement>('.dam-pet-host')?.dataset.motion !== 'drag');
}
async function watchRelease(page: Page, selector: string) {
    await page.evaluate(selector => {
        const root = document.querySelector<HTMLElement>(selector)!;
        const states: { motion: string; cell?: string; eyes?: string; eyelids?: string }[] = [];
        const observer = new MutationObserver(() => {
            const art = root.querySelector<HTMLElement>('[data-current-motion]');
            const state = { motion: root.dataset.motion ?? art?.dataset.currentMotion ?? '', cell: art?.dataset.cell,
                eyes: art?.dataset.eyes, eyelids: art?.dataset.eyelids };
            if (JSON.stringify(state) !== JSON.stringify(states.at(-1))) states.push(state);
        });
        observer.observe(root, { subtree: true, attributes: true });
        (window as any).slideTrace = { states, observer };
    }, selector);
}
async function checkRelease(page: Page, species: string, slid = true) {
    const states = await page.evaluate(() => {
        const trace = (window as any).slideTrace; trace.observer.disconnect(); return trace.states;
    });
    const sliding = states.filter(state => state.motion === 'slide');
    assert.equal(sliding.length > 0, slid, slid ? 'observed the slide' : 'a stationary drop never slides');
    assert.ok(sliding.every(state => (species === 'snek' ? state.eyes === 'shocked' : state.cell === '5') && state.eyelids === '0.00'), 'the off-balance pose stays startled throughout the glide');
    assert.ok(!states.some(state => ['wobble', 'land', 'happy', 'hop'].includes(state.motion)), 'no recovery effect or immediate flourish after sliding');
    assert.equal(states.at(-1).motion, 'idle', 'the glide returns directly to idle');
}
try {
    const page = await browser.newPage({ viewport: { width: 1360, height: 1000 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base); await page.waitForFunction(() => document.querySelector('.dam-pet-host')?.getAttribute('data-motion') === 'idle');
    for (const species of ['snek', 'silkie']) {
        await reset(page, species); await scaredDrag(page, species);
        await reset(page, species);
        await drag(page, 30, 0, 0, 60);
        await page.locator('.dam-pet-host[data-motion="slide"]').waitFor();
        const gentle = await position(page); await page.waitForTimeout(180);
        assert.ok((await position(page))!.x > gentle!.x + 5, species + ' gentle shove coasts');
        assert.equal((await position(page))!.y, gentle!.y, 'a sideways shove has no gravity');
        assert.deepEqual(await page.evaluate(() => (window as any).petTest.calls), [], 'drag never awards affection');

        await reset(page, species); await watchRelease(page, '.dam-pet-host'); await drag(page, 0, -64, 0, 60);
        await page.locator('.dam-pet-host[data-motion="slide"]').waitFor();
        const slowUp = await position(page); await page.waitForTimeout(180);
        const coastingUp = await position(page);
        assert.ok(coastingUp!.y < slowUp!.y - 10, 'a slower upward drag slides upward');
        await page.locator('.dam-pet-host[data-motion="idle"]').waitFor();
        await checkRelease(page, species);
        const slideEnd = await position(page);
        assert.ok(slideEnd!.y < coastingUp!.y, 'the upward slide settles without curving down');
        const slideSaved = await page.evaluate(() => JSON.parse(localStorage.getItem('dam-pet-position:throw-preview')!));
        assert.ok(Math.abs(slideSaved.y * 1000 - slideEnd!.y) < 1, 'slide resting position is persisted');

        await reset(page, species); await drag(page, 0, 80);
        await page.locator('.dam-pet-host[data-motion="slide"]').waitFor();
        const downward = await position(page); await page.waitForTimeout(100);
        assert.ok((await position(page))!.y > downward!.y + 10, 'a fast downward flick slides down');

        await reset(page, species); await watchRelease(page, '.dam-pet-host'); await drag(page, 100, -100, 180);
        await page.locator('.dam-pet-host[data-motion="idle"]').waitFor();
        const held = await position(page); await page.waitForTimeout(180);
        assert.equal((await position(page))!.y, held!.y, 'holding before release stops the throw');
        await checkRelease(page, species, false);

        await reset(page, species); await drag(page, 100, -100);
        await page.locator('.dam-pet-host[data-motion="flap"]').waitFor();
        if (species === 'snek') await page.locator('.dam-pet-art [data-pose="air"]').waitFor();
        else await page.locator('.dam-pet-host[data-motion="flap"] .dam-pet-art [data-cell="10"]').waitFor();
        const release = await position(page);
        await page.waitForTimeout(150);
        const rising = await position(page);
        assert.ok(rising!.x > release!.x + 20 && rising!.y < release!.y - 20, species + ' carries upward diagonal momentum');
        await page.locator('.dam-pet-host[data-motion="toss"]').waitFor();
        if (species === 'silkie') assert.equal(await page.locator('.dam-pet-art [data-cell]').getAttribute('data-cell'), '10', 'both wings stay spread during descent');
        await page.locator('.dam-pet-host[data-motion="land"]').waitFor();
        const landed = await position(page);
        assert.ok(landed!.y > release!.y + 50, 'gravity lands a little below release');
        const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('dam-pet-position:throw-preview')!));
        assert.ok(Math.abs(saved.x * 1360 - landed!.x) < 1 && Math.abs(saved.y * 1000 - landed!.y) < 1, 'final resting position is persisted');

        await reset(page, species); await drag(page, -100, 0);
        const sideways = await position(page); await page.waitForTimeout(120);
        const continuing = await position(page);
        assert.ok(continuing!.x < sideways!.x - 20 && continuing!.y === sideways!.y, 'left shove travels left without curving downward');
        await page.locator('.dam-pet-body').click({ button: 'right', force: true });
        const stopped = await position(page); await page.waitForTimeout(120);
        assert.equal((await position(page))!.x, stopped!.x, 'opening the menu catches the pet');
        assert.deepEqual(await page.evaluate(() => (window as any).petTest.calls), ['OpenMenu']);
        console.log('PASS ' + species + ': scared drag, gentle/upward/downward/sideways shoves, fast upward throw, persistence and menu catch');
    }
    for (const species of ['silkie', 'snek']) { await reset(page, species, 1, true); await scaredDrag(page, species); }
    await reset(page, 'silkie', 1, true); await drag(page, 100, -100);
    const reduced = await position(page); await page.waitForTimeout(250);
    assert.equal((await position(page))!.y, reduced!.y, 'reduced motion places the pet without a flight');
    await reset(page, 'silkie', 1, true); await drag(page, 100, 0);
    const reducedSlide = await position(page); await page.waitForTimeout(150);
    assert.equal((await position(page))!.x, reducedSlide!.x, 'reduced motion also disables shove coasting');
    await page.emulateMedia({ reducedMotion: 'reduce' }); await reset(page, 'snek'); await drag(page, 100, -100);
    const systemReduced = await position(page); await page.waitForTimeout(150);
    assert.equal((await position(page))!.y, systemReduced!.y, 'system reduced motion also disables flight');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await reset(page); await drag(page, 100, -100);
    const movingBody = await page.locator('.dam-pet-body').boundingBox();
    await page.mouse.move(movingBody!.x + 70, movingBody!.y + 60); await page.mouse.down();
    const caught = await position(page); await page.waitForTimeout(180);
    assert.equal((await position(page))!.x, caught!.x, 'grabbing in flight catches the pet');
    await page.mouse.up();

    await reset(page);
    const hideBody = await page.locator('.dam-pet-body').boundingBox();
    await page.mouse.move(hideBody!.x + 70, hideBody!.y + 60); await page.mouse.down();
    await page.mouse.move(hideBody!.x + 80, hideBody!.y + 60);
    const hideTarget = await page.locator('.dam-pet-hide-target').boundingBox();
    await page.mouse.move(hideTarget!.x + hideTarget!.width / 2, hideTarget!.y + hideTarget!.height / 2);
    await page.mouse.up();
    assert.deepEqual(await page.evaluate(() => (window as any).petTest.calls), ['Dismiss'], 'drag-to-hide wins over throwing');
    const dismissed = await position(page); await page.waitForTimeout(150);
    assert.equal((await position(page))!.x, dismissed!.x, 'dismissal never launches a throw');

    await reset(page); await drag(page, 100, -100);
    await page.evaluate(() => { void (window as any).petTest.depart(); });
    const departing = await position(page); await page.waitForTimeout(150);
    assert.equal((await position(page))!.x, departing!.x, 'farewell stops a throw before crying');
    await page.locator('.dam-pet-host[data-departing="true"][data-motion="sad"]').waitFor();

    await reset(page); await drag(page, 100, -100);
    await page.evaluate(() => (window as any).petTest.hide());
    const hidden = await position(page); await page.waitForTimeout(150);
    assert.equal((await position(page))!.x, hidden!.x, 'hiding stops the trajectory');
    assert.deepEqual(errors, []);
    console.log('PASS reduced motion, catching, drag-to-hide, farewell and hidden-host lifecycle');

    const touchContext = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const phone = await touchContext.newPage(); phone.on('pageerror', error => errors.push(error.message));
    await phone.goto(base); await phone.waitForFunction(() => !!(window as any).petTest);
    await reset(phone, 'snek', 1);
    const touch = await touchContext.newCDPSession(phone);
    async function swipe(cancel: boolean) {
        const body = await phone.locator('.dam-pet-body').boundingBox();
        const x = body!.x + 70, y = body!.y + 60;
        // Gesture speed comes from device samples, independently of browser/transport scheduling.
        const timestamp = Date.now() / 1000;
        await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', timestamp, touchPoints: [{ x, y }] });
        for (let i = 1; i <= 4; i++) {
            await phone.waitForTimeout(18);
            await touch.send('Input.dispatchTouchEvent', { type: 'touchMove', timestamp: timestamp + i * .02, touchPoints: [{ x: x + i * 10, y: y - i * 45 }] });
        }
        await touch.send('Input.dispatchTouchEvent', { type: cancel ? 'touchCancel' : 'touchEnd', timestamp: timestamp + .085, touchPoints: [] });
    }
    await swipe(false);
    await phone.locator('.dam-pet-host[data-motion="flap"]').waitFor();
    await phone.setViewportSize({ width: 320, height: 568 });
    await phone.locator('.dam-pet-host[data-motion="land"]').waitFor();
    const phoneLand = await position(phone);
    assert.ok(phoneLand!.x >= 0 && phoneLand!.y >= 0 && phoneLand!.x + phoneLand!.width <= 321 && phoneLand!.y + phoneLand!.height <= 569, 'phone throw stays in the resized viewport: ' + JSON.stringify(phoneLand));
    await reset(phone, 'snek', 1); await swipe(true);
    const cancelled = await position(phone); await phone.waitForTimeout(180);
    assert.equal((await position(phone))!.x, cancelled!.x, 'touch cancellation never throws');
    assert.deepEqual(await phone.evaluate(() => (window as any).petTest.calls), []);
    await touchContext.close();
    console.log('PASS phone touch flick, resize and cancelled gesture');

    // The downloadable file runs offline and exposes every new pose for every stage.
    await page.goto('file://' + path.resolve('.validation/pet-playground.html'));
    await page.waitForFunction(() => !!(window as any).damagochiDemo);
    for (const species of ['silkie', 'snek']) {
        await page.evaluate(id => (window as any).damagochiDemo.choosePet(id), species);
        for (const stage of [1, 2, 3]) {
            await page.locator('[data-stage="' + stage + '"]').click();
            for (const motion of ['drag', 'slide', 'wobble', 'flap', 'toss', 'land']) {
                await page.locator('[data-motion="' + motion + '"]').click();
                await page.waitForTimeout(60);
                assert.equal(await page.locator('#main-pet [data-current-motion]').getAttribute('data-current-motion'), motion);
                if (motion === 'slide') {
                    await page.waitForTimeout(450);
                    const face = species === 'snek' ? '[data-eyes="shocked"]' : '[data-cell="5"]';
                    await page.locator('#main-pet [data-current-motion="slide"]' + face).waitFor();
                }
            }
        }
        async function resetBuddy() {
            await page.evaluate(() => {
                const s = (window as any).damagochiDemo.session;
                s.action('idle', false); s.showBuddy = true;
                const buddy = document.getElementById('buddy')!;
                buddy.hidden = false; buddy.style.left = '400px'; buddy.style.top = '500px'; buddy.style.right = 'auto'; buddy.style.bottom = 'auto';
            });
        }
        await resetBuddy(); await watchRelease(page, '#buddy-pet');
        const buddy = await page.locator('#buddy-pet').boundingBox();
        await page.mouse.move(buddy!.x + 50, buddy!.y + 50); await page.mouse.down();
        await page.mouse.move(buddy!.x + 65, buddy!.y + 50);
        await page.locator('#buddy-pet [data-current-motion="drag"]').waitFor();
        await page.locator('#buddy-pet ' + (species === 'snek' ? '[data-eyes="shocked"]' : '[data-cell="5"]')).waitFor();
        await page.waitForTimeout(150); await page.mouse.up();
        await page.waitForFunction(() => (window as any).damagochiDemo.session.buddyMotion === 'idle');
        await page.locator('#buddy-pet [data-current-motion="idle"]').waitFor();
        await checkRelease(page, species, false);
        await resetBuddy(); await watchRelease(page, '#buddy-pet'); await drag(page, 0, -64, 0, 60, '#buddy-pet');
        await page.waitForFunction(() => (window as any).damagochiDemo.session.buddyMotion === 'slide');
        assert.equal(await page.evaluate(() => !!(window as any).damagochiDemo.session.flight), false, 'playground slow upward gesture slides');
        await page.waitForFunction(() => (window as any).damagochiDemo.session.buddyMotion === 'idle');
        await page.locator('#buddy-pet [data-current-motion="idle"]').waitFor();
        await checkRelease(page, species);
        await resetBuddy(); await drag(page, 0, 100, 0, 18, '#buddy-pet');
        await page.waitForFunction(() => (window as any).damagochiDemo.session.buddyMotion === 'slide');
        assert.equal(await page.evaluate(() => !!(window as any).damagochiDemo.session.flight), false, 'playground downward flick slides');
        await resetBuddy(); await drag(page, 100, -100, 0, 18, '#buddy-pet');
        await page.waitForFunction(() => !!(window as any).damagochiDemo.session.flight);
        await page.getByRole('button', { name: 'Coil landing', exact: true }).or(page.getByRole('button', { name: 'Landing', exact: true })).click();
    }
    await page.evaluate(() => { (window as any).damagochiDemo.session.showBuddy = false; document.getElementById('buddy')!.hidden = true; });
    await page.screenshot({ path: '.validation/damagochi-throw-playground.png', fullPage: true });
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.waitForTimeout(250);
    await page.screenshot({ path: '.validation/damagochi-throw-playground-dark.png', fullPage: true });
    assert.deepEqual(errors, []);
    console.log('PASS offline playground: all new poses at all three stages for both species');
} finally { await browser.close(); await new Promise<void>(resolve => server.close(() => resolve())); }
