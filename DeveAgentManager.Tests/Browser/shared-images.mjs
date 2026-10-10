const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

const browser = await chromium.launch();
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, isMobile: true, hasTouch: true });
    await context.addInitScript(() => {
        window.imageAnimations = [];
        const animate = Element.prototype.animate;
        Element.prototype.animate = function (frames, options) {
            if (this.matches('.shared-image-preview, .shared-image-neighbor')) imageAnimations.push({ frames, options });
            return animate.call(this, frames, options);
        };
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && message.text().includes('Unhandled exception')) errors.push(message.text()); });
    const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const sessionId = '00000000-0000-0000-0000-000000000001';
    const now = new Date().toISOString();
    let images = ['Newest snapshot', 'Middle snapshot', 'Oldest snapshot'].map((caption, index) => ({
        id: `00000000-0000-0000-0000-00000000000${index + 2}`, sessionId, caption, fileName: `snapshot-${index}.png`,
        contentType: 'image/png', bytes: 2048, createdAt: new Date(Date.now() - index * 60000).toISOString()
    }));
    const imageBodies = [];
    for (const image of images) {
        const data = await page.evaluate(caption => {
            const canvas = document.createElement('canvas'); canvas.width = 720;
            canvas.height = caption.startsWith('Oldest') ? 900 : caption.startsWith('Newest') ? 240 : 420;
            const ctx = canvas.getContext('2d');
            ctx.fillStyle = '#182239'; ctx.fillRect(0, 0, canvas.width, canvas.height);
            ctx.fillStyle = '#95b9ff'; ctx.font = 'bold 38px sans-serif'; ctx.fillText(caption, 50, 180);
            ctx.fillStyle = '#d0d9e9'; ctx.font = '22px sans-serif'; ctx.fillText('Shared-image viewer test fixture', 50, 230);
            return canvas.toDataURL('image/png').split(',')[1];
        }, image.caption);
        imageBodies.push(Buffer.from(data, 'base64'));
    }
    await page.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === `/api/sessions/${sessionId}/images`) return route.fulfill({ json: images });
        const index = images.findIndex(image => path === `/api/sessions/${sessionId}/images/${image.id}`);
        if (index >= 0) return route.fulfill({ contentType: 'image/png', body: imageBodies[index] });
        if (path === `/api/sessions/${sessionId}`) return route.fulfill({ json: { id: sessionId, title: 'Image navigation', harnessId: 'codex', access: 'Owner', ownerName: 'Test user', messageCount: 0, createdAt: now, updatedAt: now } });
        if (path === `/api/sessions/${sessionId}/content`) return route.fulfill({ json: { format: 'deve-session', version: 1, entries: [], sources: [] } });
        return route.continue();
    });
    const touch = await context.newCDPSession(page);
    const swipe = async (dx, dy = 0) => {
        const box = await page.locator('.shared-image-stage').boundingBox();
        const x = box.x + box.width / 2, y = box.y + box.height / 2;
        await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
        for (let step = 1; step <= 6; step++) {
            await touch.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: x + dx * step / 6, y: y + dy * step / 6 }] });
            await page.waitForTimeout(35);
        }
        if (Math.abs(dx) > 40 && dy === 0) {
            assert.ok(await page.locator('.shared-image-preview').evaluate(image => Math.abs(new DOMMatrixReadOnly(getComputedStyle(image).transform).m41) > 0), 'image should follow the finger');
        }
        await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
        await page.waitForFunction(() => !document.querySelector('.shared-image-stage')?.hasAttribute('data-image-busy'));
    };

    for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + `/sessions/${sessionId}`);
        const cards = page.locator('.session-image-card');
        await cards.first().waitFor({ timeout: 60000 });
        assert.deepEqual(await cards.locator('span').allTextContents(), images.map(image => image.caption));
        assert.ok(await cards.first().evaluate(card => card.querySelector('span').getBoundingClientRect().width > card.getBoundingClientRect().width / 2), 'gallery captions should sit below the image');
        await page.getByRole('button', { name: 'View Middle snapshot', exact: true }).click();
        await page.locator('.shared-image-stage[data-image-ready="true"]').waitFor();
        const preview = page.locator('.shared-image-preview');
        const assertImage = async index => {
            await page.waitForFunction(({ id, caption }) => {
                const image = document.querySelector('.shared-image-preview');
                return image?.getAttribute('src').endsWith(id) && image.alt === caption && image.complete && image.naturalWidth > 0
                    && !document.querySelector('.shared-image-stage')?.hasAttribute('data-image-busy');
            }, { id: images[index].id, caption: images[index].caption });
            assert.equal(await preview.getAttribute('alt'), images[index].caption);
            assert.ok((await page.getByRole('link', { name: 'Download', exact: true }).getAttribute('href')).endsWith(`${images[index].id}/download`));
            assert.equal(await page.getByRole('button', { name: 'Previous image', exact: true }).isDisabled(), index === 0);
            assert.equal(await page.getByRole('button', { name: 'Next image', exact: true }).isDisabled(), index === 2);
            await page.getByRole('dialog').getByRole('heading', { name: images[index].caption, exact: true }).waitFor();
        };
        await assertImage(1);
        await page.keyboard.press('ArrowRight'); await assertImage(2);
        assert.ok((await page.evaluate(() => imageAnimations.at(-2).frames.at(-1).transform)).includes('-'), 'next animation should slide left');
        await page.keyboard.press('ArrowRight'); await assertImage(2);
        await page.keyboard.press('ArrowLeft'); await assertImage(1);
        assert.ok(!(await page.evaluate(() => imageAnimations.at(-2).frames.at(-1).transform)).includes('-'), 'previous animation should slide right');
        await page.getByRole('button', { name: 'Previous image', exact: true }).click(); await assertImage(0);
        await page.keyboard.press('ArrowLeft'); await assertImage(0);
        await page.getByRole('button', { name: 'Next image', exact: true }).click(); await assertImage(1);
        assert.equal(await page.evaluate(() => imageAnimations.at(-1).options.duration), 260, 'buttons should animate too');
        await page.keyboard.press('ArrowLeft');
        await page.keyboard.press('ArrowRight');
        await assertImage(1);
        if (width <= 390) {
            const box = await page.locator('.shared-image-stage').boundingBox();
            const x = box.x + box.width / 2, y = box.y + box.height / 2;
            await touch.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: x - 20, y, id: 1 }, { x: x + 20, y, id: 2 }] });
            await touch.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
            await assertImage(1);
            assert.ok((await page.locator('.shared-image-stage').evaluate(stage => getComputedStyle(stage).touchAction)).includes('pinch-zoom'), 'native pinch zoom must remain available');
            await swipe(-90); await assertImage(2);
            await swipe(90); await assertImage(1);
            await swipe(12); await assertImage(1);
            await swipe(0, 90); await assertImage(1);
            await swipe(90); await assertImage(0);
            await swipe(90); await assertImage(0);
            await swipe(-90); await assertImage(1);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'swipes must not expand the page');
            await page.emulateMedia({ reducedMotion: 'reduce' });
            const animations = await page.evaluate(() => imageAnimations.length);
            await page.getByRole('button', { name: 'Next image', exact: true }).click(); await assertImage(2);
            await page.keyboard.press('ArrowLeft'); await assertImage(1);
            assert.equal(await page.evaluate(() => imageAnimations.length), animations, 'reduced motion must skip sliding animations');
            await page.emulateMedia({ reducedMotion: 'no-preference' });
        }
        const dialogBox = await page.getByRole('dialog').boundingBox();
        assert.ok(dialogBox.x >= 0 && dialogBox.x + dialogBox.width <= width + 1, `preview overflows at ${width}`);
        const actions = await page.locator('.mud-dialog-actions').evaluate(element => [...element.children].map(child => child.getBoundingClientRect().right));
        assert.ok(actions.every(right => right <= width), `preview actions overflow at ${width}`);
        await page.screenshot({ path: `/tmp/dam-shared-images-${width}.png` });
        if (width <= 390) assert.ok((await preview.screenshot()).length > 800, 'the preview must remain visibly rendered after transitions');
        await page.getByRole('button', { name: 'Close', exact: true }).last().click();
        await preview.waitFor({ state: 'detached' });
        await page.keyboard.press('ArrowRight');
    }

    images = images.slice(0, 1);
    await page.reload();
    await page.getByRole('button', { name: 'View Newest snapshot', exact: true }).click();
    await page.locator('.shared-image-preview').waitFor();
    await page.locator('.shared-image-stage[data-image-ready="true"]').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Next image', exact: true }).count(), 0);
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.locator('.shared-image-preview').getAttribute('alt'), 'Newest snapshot');
    await swipe(-90);
    assert.equal(await page.locator('.shared-image-preview').getAttribute('alt'), 'Newest snapshot');
    await page.getByRole('button', { name: 'Close', exact: true }).last().click();
    // Closing while an animation is running must cancel it and remove the input listeners.
    images = ['Newest snapshot', 'Middle snapshot', 'Oldest snapshot'].map((caption, index) => ({
        id: `00000000-0000-0000-0000-00000000000${index + 2}`, sessionId, caption, fileName: `snapshot-${index}.png`,
        contentType: 'image/png', bytes: 2048, createdAt: now
    }));
    await page.reload();
    await page.getByRole('button', { name: 'View Middle snapshot', exact: true }).click();
    await page.locator('.shared-image-stage[data-image-ready="true"]').waitFor();
    await page.keyboard.press('ArrowRight');
    await page.locator('.shared-image-stage[data-image-busy="true"]').waitFor();
    await page.getByRole('button', { name: 'Close', exact: true }).last().click();
    await page.locator('.shared-image-preview').waitFor({ state: 'detached' });
    await page.keyboard.press('ArrowLeft');
    assert.deepEqual(errors, []);
    console.log('Passed: shared-image order, animated buttons/keyboard, touch swipes, finger tracking, short/vertical/edge gestures, reduced motion, titles/downloads, mid-animation disposal and single images at desktop and phone widths.');
} finally {
    await browser.close();
}
