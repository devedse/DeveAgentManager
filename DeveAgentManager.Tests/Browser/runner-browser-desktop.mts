// A Runner's browser desktop on its page: Open desktop (a new tab through the Manager's own link) for browser desktops,
// the existing view for VM desktops, Add desktop under Operations for headless Runners, both themes and phone width.
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:19081';
const id = '00000000-0000-0000-0000-000000000041';
const browser = await chromium.launch();
const at = new Date().toISOString();
try {
    for (const theme of ['light', 'dark']) {
        const context = await browser.newContext({ viewport: { width: 1400, height: 1000 }, colorScheme: theme as 'light' | 'dark' });
        await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
        const page = await context.newPage();
        page.setDefaultTimeout(60000);
        const errors: string[] = [];
        page.on('pageerror', e => errors.push(e.message));
        await page.goto(base + '/login');
        await page.locator('input[name="username"]').fill('admin');
        await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
        const info = await (await context.request.get(base + '/api/info')).json();
        const me = await (await context.request.get(base + '/api/me')).json();

        let kind = 'Desktop';
        let browserDesktop = true;
        let templateId = 'container-desktop';
        const posts: string[] = [];
        const runner = () => ({ id, name: 'desktop-runner', ownerId: me.id, ownerName: 'Admin', access: 'Owner', providerId: 'proxmox', templateId,
            kind, state: 'Ready', machineId: '30004', host: 'runner.test', username: 'runneruser', sshPort: 22, cores: 4, memoryMb: 8192,
            diskGb: 64, agents: [], harnessIds: ['claude'], toolIds: [], setupVersion: info.setupVersion, createdAt: at, updatedAt: at,
            status: { powerState: 'Running', detail: 'test-node' }, gpuPassthrough: false, browserDesktop });
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            if (route.request().method() === 'POST') {
                assert.equal(path, `/api/runners/${id}/desktop/add`, 'The page only ever starts Add desktop here');
                posts.push(path);
                return route.fulfill({ status: 202, json: { runnerIds: [id] } });
            }
            const body = path === '/api/info' ? info : path === '/api/runners' ? [runner()] : path === `/api/runners/${id}` ? runner()
                : path === `/api/runners/${id}/desktop` ? { state: 'ready', message: 'Desktop ready', sessionType: 'x11', display: ':1', automationAvailable: true }
                : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
            return route.fulfill({ json: body });
        });

        for (const width of [1400, 390]) {
            await page.setViewportSize({ width, height: 1000 });

            // A browser desktop: Open desktop goes to the Manager's own link, in a new tab.
            kind = 'Desktop'; browserDesktop = true; templateId = 'container-desktop';
            await page.goto(base + `/runners/${id}`);
            const open = page.getByRole('link', { name: 'Open desktop' });
            await open.waitFor();
            assert.equal(await open.getAttribute('href'), `editor/runners/${id}/desktop`);
            assert.equal(await open.getAttribute('target'), '_blank');
            await page.getByText('Desktop ready').waitFor();
            await page.getByText('CT 30004', { exact: true }).waitFor();
            assert.equal(await page.getByText('Add desktop', { exact: true }).count(), 0, 'A desktop Runner has nothing to add');
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'The header fits the page');
            if (width === 390) await page.screenshot({ path: `/tmp/dam-browser-desktop-${theme}.png`, fullPage: true });

            // A VM desktop keeps its own (view-only) Desktop page.
            templateId = 'desktop'; browserDesktop = false;
            await page.goto(base + `/runners/${id}`);
            await page.getByRole('link', { name: 'Desktop', exact: true }).waitFor();
            assert.equal(await page.getByRole('link', { name: 'Open desktop' }).count(), 0);

            // A headless Runner: Add desktop asks first, then starts it.
            kind = 'Headless'; templateId = 'container';
            await page.goto(base + `/runners/${id}`);
            await page.getByText('Add desktop', { exact: true }).waitFor();
            assert.equal(await page.getByRole('link', { name: /Desktop/ }).count(), 0);
            const before = posts.length;
            await page.locator('.operation', { hasText: 'Add desktop' }).getByRole('button', { name: 'Add', exact: true }).click();
            const dialog = page.getByRole('dialog');
            await dialog.getByText("Add a desktop to 'desktop-runner'?").waitFor();
            await dialog.getByRole('button', { name: 'Cancel' }).click();
            await page.waitForTimeout(300);
            assert.equal(posts.length, before, 'Cancel adds nothing');
            await page.locator('.operation', { hasText: 'Add desktop' }).getByRole('button', { name: 'Add', exact: true }).click();
            await page.getByRole('dialog').getByRole('button', { name: 'Add desktop' }).click();
            for (let i = 0; i < 50 && posts.length === before; i++) await page.waitForTimeout(100);
            assert.equal(posts.length, before + 1);
        }
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log('Passed: Open desktop for browser desktops, the view for VM desktops, Add desktop with its question, both themes and phone layouts.');
} finally {
    await browser.close();
}
