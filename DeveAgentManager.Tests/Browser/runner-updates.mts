// Cached update status is independent of the Runner load and never turns failed/unknown checks into zero updates.
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:19081';
const id = '00000000-0000-0000-0000-000000000001';
const browser = await chromium.launch({ headless: true });
const at = new Date().toISOString();
try {
    for (const theme of ['light', 'dark']) {
        const context = await browser.newContext({ viewport: { width: 1400, height: 1100 }, colorScheme: theme });
        await context.addInitScript(theme => localStorage.setItem('dam-theme', theme), theme);
        const page = await context.newPage();
        if (process.env.DAM_TEST_CPU_RATE) {
            const cdp = await context.newCDPSession(page);
            await cdp.send('Emulation.setCPUThrottlingRate', { rate: Number(process.env.DAM_TEST_CPU_RATE) });
        }
        const errors = [];
        const responses = [];
        await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
        page.on('pageerror', e => errors.push(e.message));
        page.on('response', r => { if (new URL(r.url()).pathname.startsWith('/api/')) responses.push({ url: new URL(r.url()).pathname, status: r.status() }); });
        try {
        await page.goto(base + '/login');
        await page.locator('input[name="username"]').fill('admin');
        await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
        const info = await (await context.request.get(base + '/api/info')).json();
        const me = await (await context.request.get(base + '/api/me')).json();
        info.providers = info.providers.map(p => ({ ...p, capabilities: { ...p.capabilities, canStartStop: true } }));
        let updates: any = null;
        let state = 'Ready';
        let requests = 0;
        let holdRead = false;
        let readStarted: () => void;
        let releaseRead: () => void;
        let readFinished: () => void;
        let emitChange: () => void;
        // HTTP fixtures and live events describe the same Runner; no real server event races a mocked response.
        await context.routeWebSocket('**/hubs/live**', socket => {
            const send = value => socket.send(JSON.stringify(value) + '\u001e');
            socket.onMessage(message => {
                for (const frame of String(message).split('\u001e').filter(Boolean)) {
                    const value = JSON.parse(frame);
                    if (value.protocol) send({});
                    else if (value.type === 6) send({ type: 6 });
                    else if (value.invocationId) send({ type: 3, invocationId: value.invocationId, result: [] });
                }
            });
            const ping = setInterval(() => send({ type: 6 }), 5000);
            socket.onClose(() => clearInterval(ping));
            emitChange = () => send({ type: 1, target: 'changed', arguments: [{ kind: 'RunnerChanged', runnerId: id }] });
        });
        const observed = {
            software: [
                { id: 'claude', name: 'Claude Code', installedVersion: '2.1.0', latestVersion: '2.2.0', updateAvailable: true },
                { id: 'codex', name: 'Codex', installedVersion: '0.163.0', latestVersion: '0.164.0', updateAvailable: true },
                { id: 'grok', name: 'Grok', installedVersion: '1.0.67', latestVersion: '1.0.70', updateAvailable: true },
                { id: 'vscode', name: 'VS Code', installedVersion: '4.100.0', latestVersion: '4.100.0', updateAvailable: false }
            ], softwareCheckedAt: at, systemPackageCount: 6, systemCheckedAt: at, checking: false, rebootRequired: true
        };
        const runner = () => ({ id, name: 'build-runner', ownerId: me.id, ownerName: 'Admin', access: 'Owner', providerId: 'existing', kind: 'Headless', state,
            machineId: '30001', host: 'runner.test', username: 'runneruser', sshPort: 22, cores: 4, memoryMb: 8192, diskGb: 64,
            agents: [], harnessIds: ['claude', 'codex', 'grok'], toolIds: ['vscode'], setupVersion: info.setupVersion,
            createdAt: at, updatedAt: at, installedVersions: 'Claude Code 2.1.0 · Codex 0.163.0 · Grok 1.0.67', updates });
        await context.route('**/api/**', async route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
            if (route.request().method() === 'POST') {
                assert.equal(path, `/api/runners/${id}/updates/check`, 'Checking must never call an install or update endpoint');
                requests++;
                // Match RunnerUpdateCache.Request: accepted checks remain pending in subsequent reads.
                updates = { ...updates, checking: true };
                return route.fulfill({ status: 202, body: '' });
            }
            const body = path === '/api/info' ? info : path === '/api/runners' ? [runner()] : path === `/api/runners/${id}` ? runner()
                : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
            if (holdRead && path === `/api/runners/${id}`) {
                holdRead = false;
                // Snapshot before the POST, then deliberately deliver this stale GET after the POST completes.
                const snapshot = structuredClone(body);
                readStarted();
                await new Promise<void>(resolve => { releaseRead = resolve; });
                await route.fulfill({ json: snapshot });
                readFinished();
                return;
            }
            return route.fulfill({ json: body });
        });
        await page.goto(base + `/runners/${id}`);
        const status = page.getByLabel('Update status', { exact: true });
        await status.getByRole('status').waitFor({ timeout: 60000 });
        assert.doesNotMatch(await status.innerText(), /up to date|0 updates/i);
        updates = structuredClone(observed);
        await page.reload();
        await status.getByText('3 tool updates · 6 system updates', { exact: true }).waitFor({ timeout: 60000 });
        const headerUpdate = page.locator('.page-header-actions').getByRole('button', { name: 'Update all', exact: true });
        assert.match(await headerUpdate.innerText(), /3 tools/);
        assert.match(await headerUpdate.innerText(), /6 system/);
        await page.locator('.operation', { hasText: 'Update Codex' }).getByText('0.164.0', { exact: true }).waitFor();
        await status.getByText('Restart needed', { exact: true }).waitFor();
        await page.screenshot({ path: `/tmp/dam-runner-updates-detail-${theme}.png`, fullPage: true });
        const staleStarted = new Promise<void>(resolve => { readStarted = resolve; });
        const staleFinished = new Promise<void>(resolve => { readFinished = resolve; });
        holdRead = true;
        emitChange();
        let timeout: ReturnType<typeof setTimeout>;
        try {
            await Promise.race([staleStarted, new Promise((_, reject) => {
                timeout = setTimeout(() => reject(new Error('Controlled live refresh did not request the Runner')), 10000);
            })]);
        } finally { clearTimeout(timeout); }
        await Promise.all([
            page.waitForResponse(r => new URL(r.url()).pathname === `/api/runners/${id}/updates/check`),
            page.getByRole('button', { name: 'Check now', exact: true }).click()
        ]);
        assert.equal(requests, 1);
        await page.locator('button:disabled', { hasText: 'Check now' }).waitFor();
        await page.evaluate(() => {
            (window as any).__checkReenabled = false;
            const observer = new MutationObserver(() => {
                const button = [...document.querySelectorAll('button')].find(button => button.textContent.includes('Check now'));
                if (button && !button.disabled) (window as any).__checkReenabled = true;
            });
            observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled'] });
            (window as any).__stopCheckObserver = () => observer.disconnect();
        });
        releaseRead();
        await staleFinished;
        // Follow with a real live refresh, observing the entire interval for a stale response re-enabling the button.
        await Promise.all([
            page.waitForResponse(r => new URL(r.url()).pathname === `/api/runners/${id}` && r.request().method() === 'GET'),
            Promise.resolve().then(() => emitChange())
        ]);
        await status.getByText(/Checking…/).waitFor();
        assert.equal(await page.evaluate(() => (window as any).__checkReenabled), false, 'An obsolete GET cannot undo an accepted update check');
        await page.evaluate(() => (window as any).__stopCheckObserver());
        // A background refresh can replace the local optimistic status with the cached server status.
        // Exercise that deterministically instead of relying on the live connection's timing.
        await page.reload();
        await status.getByText(/Checking…/).waitFor();
        assert.match(await status.innerText(), /3 tool updates · 6 system updates/, 'Refresh keeps the last observed counts while checking');
        assert.ok(await page.getByRole('button', { name: 'Check now', exact: true }).isDisabled(), 'A pending cached check cannot be started again');
        updates = { ...observed, softwareError: 'Release check unavailable', systemError: 'Repository unavailable' };
        await page.reload();
        await status.getByText(/3\+ tool updates/).waitFor({ timeout: 60000 });
        assert.doesNotMatch(await status.innerText(), /up to date/i);
        updates = { software: [], softwareError: 'Release check unavailable', systemError: 'Repository unavailable', checking: false };
        await page.reload();
        await status.getByText('Tool check incomplete · System check failed', { exact: true }).waitFor({ timeout: 60000 });
        assert.doesNotMatch(await headerUpdate.innerText(), /0/);
        updates = { ...structuredClone(observed), software: observed.software.map(s => ({ ...s, installedVersion: s.latestVersion, updateAvailable: false })), systemPackageCount: 0, rebootRequired: false };
        await page.reload();
        await status.getByText('Tools up to date · System up to date', { exact: true }).waitFor({ timeout: 60000 });
        assert.equal(await headerUpdate.locator('.runner-update-count').count(), 0, 'No zero-count badge after a complete check');
        updates = structuredClone(observed);
        state = 'Stopped';
        await page.reload();
        await headerUpdate.waitFor({ timeout: 60000 });
        assert.ok(await headerUpdate.isDisabled());
        assert.match(await status.innerText(), /3 tool updates/);
        state = 'Ready';
        await page.goto(base + '/runners');
        const card = page.locator('.runner-card').first();
        await card.locator('.runner-update-count').first().waitFor({ timeout: 60000 });
        await page.screenshot({ path: `/tmp/dam-runner-updates-cards-${theme}.png`, fullPage: true });
        const buttons = card.locator('.runner-card-actions button');
        for (const width of [320, 360, 390, 768, 1400]) {
            await page.setViewportSize({ width, height: 844 });
            await page.waitForTimeout(500); // Allow the responsive drawer and card widths to settle.
            await card.scrollIntoViewIfNeeded();
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${width}px: no horizontal scroll`);
            const labels = [];
            for (let i = 0; i < await buttons.count(); i++) {
                const box = await buttons.nth(i).boundingBox();
                assert.ok(box.x >= 0 && box.x + box.width <= width, `${width}px: each hold button stays on screen`);
                const label = await buttons.nth(i).getByText(/^(Stop|Restart|Update all)$/).boundingBox();
                labels.push(label.y + label.height / 2);
            }
            assert.ok(Math.max(...labels) - Math.min(...labels) <= 2, `${width}px: Stop, Restart and Update all share a baseline (${labels})`);
        }
        await page.setViewportSize({ width: 390, height: 844 });
        await page.waitForTimeout(500);
        await page.screenshot({ path: `/tmp/dam-runner-updates-phone-${theme}.png`, fullPage: true });
        await page.goto(base + `/runners/${id}`);
        await status.getByText('3 tool updates · 6 system updates', { exact: true }).waitFor({ timeout: 60000 });
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Runner update details fit a phone');
        assert.deepEqual(errors, []);
        await context.tracing.stop();
        } catch (error) {
            const directory = resolve('.validation/browser', `runner-updates-${theme}`);
            mkdirSync(directory, { recursive: true });
            await page.screenshot({ path: `${directory}/failure.png`, fullPage: true }).catch(() => {});
            writeFileSync(`${directory}/responses.json`, JSON.stringify({ errors, responses: responses.slice(-100) }, null, 2));
            await context.tracing.stop({ path: `${directory}/trace.zip` });
            throw error;
        } finally {
        await context.close();
        }
    }
    console.log('Passed: Runner update counts, versions, unknown and failed checks, refresh, offline observations and phone layouts in both themes.');
} finally { await browser.close(); }
