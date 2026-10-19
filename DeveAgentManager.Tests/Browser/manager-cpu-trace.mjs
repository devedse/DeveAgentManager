import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
const playwright = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const scratch = await mkdtemp(join(tmpdir(), 'dam-cpu-browser-'));
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    await page.goto(base + '/manager-settings');
    await page.getByRole('tab', { name: 'Diagnostics', exact: true }).click();
    const start = page.getByRole('button', { name: 'Capture 60-second CPU trace', exact: true });
    await start.waitFor();
    await start.click();
    await page.getByText('Recording… You can leave this page.', { exact: true }).waitFor();
    assert.equal(await start.isDisabled(), true);
    let status = await (await context.request.get(base + '/api/diagnostics/cpu')).json();
    const id = status.active.id;
    assert.equal(status.active.durationSeconds, 60);
    await page.goto(base + '/agents');
    await page.waitForTimeout(2500);
    await page.goto(base + '/manager-settings');
    await page.getByRole('tab', { name: 'Diagnostics', exact: true }).click();
    await page.getByText('Recording… You can leave this page.', { exact: true }).waitFor();
    console.log('Real Manager capture continues across navigation; waiting for its bounded 60-second recording.');
    await page.getByRole('link', { name: 'Download capture', exact: true }).waitFor({ timeout: 95000 });
    status = await (await context.request.get(base + '/api/diagnostics/cpu')).json();
    assert.equal(status.active, null);
    assert.equal(status.captures[0].state, 'ready');
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Download capture', exact: true }).click()]);
    const path = join(scratch, 'capture.zip'); await download.saveAs(path);
    execFileSync('python3', ['-c', `import json,sys,zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
 assert set(z.namelist()) == {'manager.nettrace','process.jsonl','capture.json'}
 assert z.read('manager.nettrace').startswith(b'Nettrace')
 samples = [json.loads(line) for line in z.read('process.jsonl').splitlines()]
 assert len(samples) >= 50
 assert samples[-1]['elapsedSeconds'] >= 55
 assert samples[-1]['cpuSeconds'] >= samples[0]['cpuSeconds']
 assert json.loads(z.read('capture.json'))['requestedSeconds'] == 60
`, path]);
    for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 850 }); await page.waitForTimeout(400);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}px overflow`);
        await page.screenshot({ path: `/tmp/dam-cpu-trace-${width}.png` });
    }
    await page.getByRole('button', { name: 'Delete CPU capture', exact: true }).click();
    await page.getByText('No CPU captures yet.', { exact: true }).waitFor();
    assert.equal((await context.request.get(base + `/api/diagnostics/cpu/${id}/download`)).status(), 404);
    await start.click();
    await page.getByText('Recording… You can leave this page.', { exact: true }).waitFor();
    await page.waitForTimeout(1500);
    const canceledId = (await (await context.request.get(base + '/api/diagnostics/cpu')).json()).active.id;
    await page.getByRole('button', { name: 'Cancel capture', exact: true }).click();
    await page.locator('.cpu-capture-row').getByText(/Canceled/).waitFor({ timeout: 15000 });
    assert.equal((await context.request.get(base + `/api/diagnostics/cpu/${canceledId}/download`)).status(), 404);
    await page.getByRole('button', { name: 'Delete CPU capture', exact: true }).click();
    await page.getByText('No CPU captures yet.', { exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log('Passed: real 60-second capture, navigation survival, completed ZIP with samples/metrics, cancellation, download/deletion, phone layouts.');
} finally { await browser.close(); await rm(scratch, { recursive: true, force: true }); }
