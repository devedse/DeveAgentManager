// Run against a disposable Manager: tab bars wider than the window scroll natively, and the chosen or focused tab
// stays in view, with a mouse, by touch, with the keyboard and for a link to a tab.
import assert from 'node:assert/strict';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
type Locator = import('playwright').Locator;
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const password = process.env.DAM_TEST_PASSWORD ?? 'test-only-password';
const pages = ['/settings', '/manager-settings'];
try {
    for (const touch of [false, true]) {
        for (const width of [390, 600, 1280]) {
            const how = `at ${width}px (${touch ? 'touch' : 'mouse'})`;
            const context = await browser.newContext({ viewport: { width, height: 850 }, hasTouch: touch, isMobile: touch });
            const page = await context.newPage();
            const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
            await page.goto(base + '/login');
            await page.locator('[name=username]').fill('admin');
            await page.locator('[name=password]').fill(password);
            await Promise.all([page.waitForURL(url => url.pathname !== '/login'), page.locator('button[type=submit]').click()]);
            const bar = page.locator('.mud-tabs-tabbar-content').first();
            const tabs = page.locator('.mud-tabs-tabbar').first().getByRole('tab');
            for (const path of pages) {
                await page.goto(base + path);
                await tabs.first().waitFor();
                await page.waitForTimeout(300);
                const names = (await tabs.allTextContents()).map(name => name.trim());
                assert.ok(names.length >= 5, `${path} has its tabs: ${names.join(', ')}`);
                assert.equal(await page.locator('.mud-tabs-scroll-button:visible').count(), 0, 'no arrow buttons');
                for (const name of [...names.slice(1), names[0]]) {
                    const tab = tabs.filter({ hasText: name }).first();
                    if (touch) {
                        await tab.scrollIntoViewIfNeeded();
                        await tab.tap();
                    } else {
                        // Out of sight, a person scrolls the bar to it (scrollbar or Shift+wheel) and clicks it where it shows.
                        await bar.hover();
                        for (let i = 0; i < 20 && await side(tab) !== 0; i++) await page.mouse.wheel(await side(tab) * 120, 0);
                        await tab.click({ position: { x: 8, y: 8 } });
                    }
                    await page.waitForTimeout(400);
                    assert.equal(await tab.getAttribute('aria-selected'), 'true', `${name} is chosen on ${path} ${how}`);
                    assert.equal(await side(tab), 0, `${name} stays in view on ${path} ${how}`);
                }
                assert.equal(await page.locator('.mud-tabs-tabbar-wrapper').first().evaluate(e => getComputedStyle(e).transform), 'none',
                    'only the bar scrolls; a second movement put the tabs out of sight');
            }

            if (!touch) {
                // The arrow keys move the focus along the tabs; the focused one is shown.
                await page.goto(base + '/settings');
                await tabs.first().waitFor();
                await tabs.first().focus();
                for (let i = 1; i < await tabs.count(); i++) {
                    await page.keyboard.press('ArrowRight');
                    await page.waitForTimeout(150);
                    const focused = page.locator('.mud-tab:focus');
                    assert.equal(await side(focused), 0, `focused tab ${i} is in view ${how}`);
                }
            }

            // A link to a tab that is far along the bar.
            await page.goto(base + '/manager-settings?tab=diagnostics');
            const diagnostics = tabs.filter({ hasText: 'Diagnostics' }).first();
            await page.waitForFunction(() => document.querySelector('.mud-tab[aria-selected="true"]')?.textContent?.includes('Diagnostics'));
            await page.waitForTimeout(300);
            assert.equal(await side(diagnostics), 0, `a linked tab is in view ${how}`);
            assert.deepEqual(errors, []);
            await context.close();
        }
    }
    console.log('Passed: tab bars scroll natively; chosen and focused tabs stay in view with a mouse, by touch, with the keyboard and for a link, at 390, 600 and 1280px.');

    // Below 0: the tab is (partly) left of the bar's visible part; above 0: right of it; 0: fully shown.
    async function side(tab: Locator) {
        return tab.evaluate(element => {
            const box = element.getBoundingClientRect(), bar = element.closest('.mud-tabs-tabbar-content')!.getBoundingClientRect();
            return box.left < bar.left - 1 ? -1 : box.right > bar.right + 1 ? 1 : 0;
        });
    }
} finally {
    await browser.close();
}
