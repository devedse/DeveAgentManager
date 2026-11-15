// A container Runner's GPU panel: every state, the host command only for people who manage the Runner, Set up GPU,
// no panel for VM Runners, both themes and phone width.
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:19081';
const id = '00000000-0000-0000-0000-000000000031';
const command = 'pct set 30003 --dev0 /dev/dri/renderD128,gid=993';
const byCard = (address: string) => `pct set 30003 --dev0 "$(readlink -e /dev/dri/by-path/pci-${address}-render)",gid=993`;
const integrated = { address: '0000:00:02.0', name: 'Intel Corporation Raptor Lake-P [Iris Xe Graphics]', attachCommand: byCard('0000:00:02.0') };
const discrete = { address: '0000:0b:00.0', name: 'Intel Corporation Battlemage G21 [Intel Graphics]', attachCommand: byCard('0000:0b:00.0') };
const problem = 'The token may not list the graphics cards of test-node: that needs Sys.Audit on /.';
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

        let gpu: any = null;
        let access = 'Owner';
        let passthrough = true;
        let failed = false;
        const posts: string[] = [];
        const runner = () => ({ id, name: 'gpu-runner', ownerId: me.id, ownerName: 'Admin', access, providerId: 'proxmox', templateId: 'container',
            kind: 'Headless', state: failed ? 'Error' : 'Ready', machineId: failed ? null : '30003',
            stateMessage: failed ? 'The container templates on storage \'local\' of test-node can\'t be read.' : null, host: 'runner.test', username: 'runneruser', sshPort: 22, cores: 4, memoryMb: 8192,
            diskGb: 64, agents: [], harnessIds: ['claude'], toolIds: [], setupVersion: info.setupVersion, createdAt: at, updatedAt: at,
            status: { powerState: 'Running', detail: 'test-node' }, gpuPassthrough: passthrough });
        await context.route('**/api/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            if (route.request().method() === 'POST') {
                assert.equal(path, `/api/runners/${id}/gpu/setup`, 'The panel only ever starts Set up GPU');
                posts.push(path);
                return route.fulfill({ status: 202, json: { runnerIds: [id] } });
            }
            assert.ok(passthrough || path !== `/api/runners/${id}/gpu`, 'A VM Runner never asks for its GPU');
            const body = path === '/api/info' ? info : path === '/api/runners' ? [runner()] : path === `/api/runners/${id}` ? runner()
                : path === `/api/runners/${id}/gpu` ? gpu : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
            return route.fulfill({ json: body });
        });

        for (const width of [1400, 390]) {
            await page.setViewportSize({ width, height: 1000 });
            access = 'Owner';
            // The host's cards can't be listed: the command for its first GPU, and why.
            gpu = { state: 'not-attached', message: 'No GPU is passed into this Runner yet.', attachCommand: command, hostGpusProblem: problem };
            await page.goto(base + `/runners/${id}`);
            const panel = page.locator('.runner-gpu');
            await panel.getByText('No GPU passed in').waitFor();
            await panel.getByText(command, { exact: true }).waitFor();
            await panel.getByText('As root on test-node, then restart the Runner.').waitFor();
            await panel.getByText(`${problem} This passes in the host's first GPU.`).waitFor();
            assert.equal(await panel.locator('.gpu-card').count(), 0);
            await page.getByText('CT 30003', { exact: true }).waitFor();
            assert.ok(await panel.getByRole('button', { name: 'Copy the command' }).isVisible());
            assert.equal(await panel.getByRole('button', { name: 'Set up GPU' }).count(), 0, 'Nothing to set up before a GPU is passed in');
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'The command wraps instead of widening the page');

            // Two cards: no command until one is chosen, then that card's command, kept while the panel refreshes.
            gpu = { state: 'not-attached', message: 'No GPU is passed into this Runner yet.', attachCommand: command, hostGpus: [integrated, discrete] };
            await panel.getByRole('button', { name: 'Refresh GPU status' }).click();
            await panel.locator('.gpu-card').waitFor();
            assert.equal(await panel.locator('.gpu-command').count(), 0, 'No guessed card when the host has more than one');
            await panel.locator('.gpu-card').click();
            await page.getByRole('option', { name: 'Intel Corporation Battlemage G21 [Intel Graphics] · 0b:00.0' }).click();
            await panel.getByText(discrete.attachCommand, { exact: true }).waitFor();
            await panel.getByRole('button', { name: 'Refresh GPU status' }).click();
            await page.waitForTimeout(300);
            await panel.getByText(discrete.attachCommand, { exact: true }).waitFor();
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'The card command wraps instead of widening the page');
            if (width === 390) await page.screenshot({ path: `/tmp/dam-runner-gpu-${theme}.png`, fullPage: true });

            // One card: its name and its command straight away.
            gpu = { state: 'not-attached', message: 'No GPU is passed into this Runner yet.', attachCommand: command, hostGpus: [discrete] };
            await panel.getByRole('button', { name: 'Refresh GPU status' }).click();
            await panel.getByText('Intel Corporation Battlemage G21 [Intel Graphics] · 0b:00.0', { exact: true }).waitFor();
            await panel.getByText(discrete.attachCommand, { exact: true }).waitFor();
            assert.equal(await panel.locator('.gpu-card').count(), 0);

            gpu = { state: 'drivers-missing', message: '/dev/dri/renderD128 is passed in. Set up GPU installs its drivers.', renderDevice: '/dev/dri/renderD128', attachCommand: command };
            await panel.getByRole('button', { name: 'Refresh GPU status' }).click();
            await panel.getByText('Drivers needed').waitFor();
            assert.equal(await panel.locator('.gpu-command').count(), 0, 'The command is gone once the GPU is passed in');
            const before = posts.length;
            await panel.getByRole('button', { name: 'Set up GPU' }).click();
            await page.waitForFunction(() => true);
            for (let i = 0; i < 50 && posts.length === before; i++) await page.waitForTimeout(100);
            assert.equal(posts.length, before + 1);

            gpu = { state: 'ready', message: 'Intel(R) Graphics (BMG G21)', deviceName: 'Intel(R) Graphics (BMG G21)', renderDevice: '/dev/dri/renderD128', attachCommand: command };
            await panel.getByRole('button', { name: 'Refresh GPU status' }).click();
            await panel.getByText('GPU ready').waitFor();
            await panel.getByText('/dev/dri/renderD128', { exact: true }).waitFor();
            await panel.getByRole('button', { name: 'Reinstall drivers' }).waitFor();

            // Someone who only uses the Runner sees the state, without the host command or the setup button.
            access = 'Use';
            gpu = { state: 'drivers-missing', message: '/dev/dri/renderD128 is passed in. Set up GPU installs its drivers.', renderDevice: '/dev/dri/renderD128' };
            await page.goto(base + `/runners/${id}`);
            await panel.getByText('Drivers needed').waitFor();
            assert.equal(await panel.locator('code').count(), 0);
            assert.equal(await panel.getByRole('button', { name: /Set up GPU|Reinstall drivers|Copy/ }).count(), 0);

            // A VM Runner has no GPU panel at all.
            passthrough = false;
            access = 'Owner';
            await page.goto(base + `/runners/${id}`);
            await page.getByRole('heading', { name: 'gpu-runner' }).first().waitFor();
            await page.locator('.panel').first().waitFor();
            assert.equal(await page.locator('.runner-gpu').count(), 0);
            passthrough = true;

            // Creating failed before Proxmox made anything: nothing to start or update, so it says to delete it.
            failed = true;
            await page.goto(base + `/runners/${id}`);
            await page.getByText('Nothing was created. Fix the cause, then delete this Runner and create it again.').waitFor();
            assert.equal(await page.getByText('to try again').count(), 0);
            failed = false;
        }
        assert.deepEqual(errors, []);
        await context.close();
    }
    console.log('Passed: GPU panel states, choosing the host card, host command for managers only, Set up GPU, no panel on VMs, never-created Runners, both themes and phone layouts.');
} finally {
    await browser.close();
}
