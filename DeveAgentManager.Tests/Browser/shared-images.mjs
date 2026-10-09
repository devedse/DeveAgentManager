const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
import assert from 'node:assert/strict';

const browser = await chromium.launch();
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
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
            const canvas = document.createElement('canvas'); canvas.width = 720; canvas.height = 420;
            const ctx = canvas.getContext('2d');
            ctx.fillStyle = '#182239'; ctx.fillRect(0, 0, 720, 420);
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

    for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(base + `/sessions/${sessionId}`);
        const cards = page.locator('.session-image-card');
        await cards.first().waitFor({ timeout: 60000 });
        assert.deepEqual(await cards.locator('span').allTextContents(), images.map(image => image.caption));
        assert.ok(await cards.first().evaluate(card => card.querySelector('span').getBoundingClientRect().width > card.getBoundingClientRect().width / 2), 'gallery captions should sit below the image');
        await page.getByRole('button', { name: 'View Middle snapshot', exact: true }).click();
        const preview = page.locator('.shared-image-preview');
        const assertImage = async index => {
            await page.waitForFunction(({ id }) => document.querySelector('.shared-image-preview')?.getAttribute('src').endsWith(id), { id: images[index].id });
            assert.equal(await preview.getAttribute('alt'), images[index].caption);
            assert.ok((await page.getByRole('link', { name: 'Download', exact: true }).getAttribute('href')).endsWith(`${images[index].id}/download`));
            assert.equal(await page.getByRole('button', { name: 'Previous image', exact: true }).isDisabled(), index === 0);
            assert.equal(await page.getByRole('button', { name: 'Next image', exact: true }).isDisabled(), index === 2);
            await page.getByRole('dialog').getByRole('heading', { name: images[index].caption, exact: true }).waitFor();
        };
        await assertImage(1);
        await page.keyboard.press('ArrowRight'); await assertImage(2);
        await page.keyboard.press('ArrowRight'); await assertImage(2);
        await page.keyboard.press('ArrowLeft'); await assertImage(1);
        await page.getByRole('button', { name: 'Previous image', exact: true }).click(); await assertImage(0);
        await page.keyboard.press('ArrowLeft'); await assertImage(0);
        await page.getByRole('button', { name: 'Next image', exact: true }).click(); await assertImage(1);
        const dialogBox = await page.getByRole('dialog').boundingBox();
        assert.ok(dialogBox.x >= 0 && dialogBox.x + dialogBox.width <= width + 1, `preview overflows at ${width}`);
        const actions = await page.locator('.mud-dialog-actions').evaluate(element => [...element.children].map(child => child.getBoundingClientRect().right));
        assert.ok(actions.every(right => right <= width), `preview actions overflow at ${width}`);
        await page.screenshot({ path: `/tmp/dam-shared-images-${width}.png` });
        await page.getByRole('button', { name: 'Close', exact: true }).last().click();
        await preview.waitFor({ state: 'detached' });
        await page.keyboard.press('ArrowRight');
    }

    images = images.slice(0, 1);
    await page.reload();
    await page.getByRole('button', { name: 'View Newest snapshot', exact: true }).click();
    await page.locator('.shared-image-preview').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Next image', exact: true }).count(), 0);
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.locator('.shared-image-preview').getAttribute('alt'), 'Newest snapshot');
    assert.deepEqual(errors, []);
    console.log('Passed: shared-image order, UI and keyboard navigation, boundaries, titles, downloads, disposal and single images on desktop and mobile.');
} finally {
    await browser.close();
}
