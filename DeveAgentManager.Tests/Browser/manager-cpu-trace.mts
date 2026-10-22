// Run against a disposable Manager started with CPU_TRACE_STARTUP=true and DOTNET_EventPipeThreadSamplingRate=0x32.
import assert from 'node:assert/strict';
import { mkdtemp, rm, copyFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const scratch = await mkdtemp(join(tmpdir(), 'dam-cpu-browser-'));
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    let statusRequests = 0;
    page.on('request', r => { if (new URL(r.url()).pathname === '/api/diagnostics/cpu') statusRequests++; });
    const getStatus = async () => (await context.request.get(base + '/api/diagnostics/cpu')).json();
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
    await Promise.all([page.waitForURL(url => url.pathname !== '/login'), page.locator('button[type=submit]').click()]);
    if (await page.locator('[name=currentPassword]').isVisible()) {
        await page.locator('[name=currentPassword]').fill(process.env.DAM_TEST_PASSWORD ?? 'test-only-password');
        await page.locator('[name=newPassword]').fill('test-only-cpu-browser-new-password');
        await page.locator('[name=confirmPassword]').fill('test-only-cpu-browser-new-password');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    }
    const indicator = page.locator('.cpu-capture-indicator');
    await indicator.waitFor();
    assert.match(await indicator.getAttribute('aria-label'), /Startup CPU capture/);
    await indicator.click();
    await page.waitForURL(base + '/manager-settings?tab=diagnostics');
    await page.getByRole('tab', { name: 'Diagnostics', exact: true }).waitFor();
    await page.waitForFunction(() => [...document.querySelectorAll('[role=tab]')]
        .some(tab => tab.textContent.trim() === 'Diagnostics' && tab.getAttribute('aria-selected') === 'true'));
    const start = page.getByRole('button', { name: 'Capture 60-second CPU trace', exact: true });
    await start.waitFor();
    assert.equal(await start.isDisabled(), true);
    let status = await getStatus();
    const native = status.method === 'cpu-timer';
    if (!native) await page.locator('.cpu-capture-progress').getByText('Startup CPU capture · Baseline before profiling', { exact: true }).waitFor();
    else {
        await page.getByText('CPU-clock samples · method CPU estimates', { exact: true }).waitFor();
        await page.getByRole('link', { name: 'Download current image symbols', exact: true }).waitFor();
        const symbols = await context.request.get(base + '/api/diagnostics/cpu/symbols/download', { headers: { Range: 'bytes=0-255' } });
        assert.equal(symbols.status(), 206);
        assert.equal((await symbols.body()).length, 256);
        assert.match(symbols.headers()['cache-control'], /no-store/);
    }
    const id = status.active.id;
    assert.equal(status.active.trigger, 'startup');
    assert.equal(status.active.durationSeconds, 300);
    assert.equal(status.captures.length, 1, 'early and hosted startup must share one worker and capture');
    for (const width of [1280, 390, 320]) {
        await page.setViewportSize({ width, height: 850 }); await page.waitForTimeout(400);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${width}px overflow`);
        await page.screenshot({ path: `/tmp/dam-startup-cpu-${width}.png` });
    }
    await page.goto(base + '/agents');
    await indicator.waitFor();
    assert.equal((await getStatus()).active.id, id);
    await indicator.click();
    await page.locator('.cpu-capture-progress').getByText(`Startup CPU capture · ${native ? 'Sampling CPU' : 'Sampling stacks'}`, { exact: true }).waitFor({ timeout: 60000 });
    console.log('Startup capture began automatically, remains visible across navigation, and entered stack sampling.');
    await page.locator('.cpu-capture-progress').getByText('Startup CPU capture · Baseline after profiling', { exact: true }).waitFor({ timeout: 285000 });
    console.log('Profiler stopped; the final unprofiled baseline is visible.');
    await page.getByRole('link', { name: 'Download capture', exact: true }).waitFor({ timeout: 60000 });
    status = await getStatus();
    assert.equal(status.active, null);
    assert.equal(status.captures[0].state, 'ready');
    await indicator.waitFor({ state: 'detached' });
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Download capture', exact: true }).click()]);
    const path = join(scratch, 'capture.zip'); await download.saveAs(path);
    execFileSync('python3', ['-c', `import json,sys,zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
 assert {'process.jsonl','capture.json','operations.jsonl'} <= set(z.namelist())
 samples = [json.loads(line) for line in z.read('process.jsonl').splitlines()]
 assert len(samples) >= 270
 assert samples[0]['phase'] == 'baseline-before'
 assert samples[0]['uptimeSeconds'] < 30, 'measurements must begin during startup'
 assert samples[-1]['elapsedSeconds'] >= 299
 assert samples[-1]['cpuSeconds'] >= samples[0]['cpuSeconds']
 manifest = json.loads(z.read('capture.json'))
 assert manifest['version'] == 3 and manifest['requestedSeconds'] == 300
 native = manifest['recorder'] == 'cpu-timer'
 if native:
  assert manifest['profilerVersion'] == '1.5.1'
  assert manifest['cpuSamplingIntervalMilliseconds'] == 19
  assert 'manager.nettrace' not in z.namelist()
  assert any(n.endswith('.pprof') for n in z.namelist())
  summary = json.loads(z.read('cpu-methods.json'))
  assert summary['sampledCpuNanoseconds'] > 0
  assert sum(m['selfNanoseconds'] for m in summary['methods']) == summary['sampledCpuNanoseconds']
  assert {'thread-cpu.jsonl','maps-before.txt','maps-after.txt','symbols-manifest.json'} <= set(z.namelist())
 else:
  assert z.read('manager.nettrace').startswith(b'Nettrace')
 assert manifest['configuredSamplingIntervalMilliseconds'] == 50
 phases = manifest['phases']
 assert [p['name'] for p in phases] == ['baseline-before','profiler-starting','profiling','profiler-stopping','baseline-after']
 assert phases[0]['elapsedSeconds'] >= (0 if native else 29) and phases[-1]['elapsedSeconds'] >= 29
 assert phases[2]['elapsedSeconds'] >= (269 if native else 239)
 for p in phases:
  assert p['elapsedSeconds'] > 0 and p['cpuSeconds'] >= 0 and p['averageCores'] >= 0
  assert abs(p['cpuSeconds'] / p['elapsedSeconds'] - p['averageCores']) < 0.0001
 for label in (['profiling','baseline-after'] if native else ['baseline-before','profiling','baseline-after']):
  assert sum(s['phase'] == label for s in samples) >= 20
`, path]);
    if (process.env.DAM_TEST_CPU_DOWNLOAD) await copyFile(path, process.env.DAM_TEST_CPU_DOWNLOAD);
    await page.waitForTimeout(2500);
    const idleRequests = statusRequests;
    await page.waitForTimeout(3000);
    assert.equal(statusRequests, idleRequests, 'idle capture UI must stop polling');
    await page.getByRole('button', { name: 'Delete CPU capture', exact: true }).click();
    await page.getByText('No CPU captures yet.', { exact: true }).waitFor();
    assert.equal((await context.request.get(base + `/api/diagnostics/cpu/${id}/download`)).status(), 404);
    await start.click();
    await page.locator('.cpu-capture-progress').getByText(`CPU capture · ${native ? 'Sampling CPU' : 'Baseline before profiling'}`, { exact: true }).waitFor();
    await indicator.waitFor();
    await page.waitForTimeout(1500);
    status = await getStatus();
    assert.equal(status.active.durationSeconds, 60);
    assert.equal(status.active.trigger, 'manual');
    const canceledId = status.active.id;
    await page.getByRole('button', { name: 'Cancel capture', exact: true }).click();
    await page.locator('.cpu-capture-row').getByText(/Canceled/).waitFor({ timeout: 15000 });
    await indicator.waitFor({ state: 'detached' });
    assert.equal((await context.request.get(base + `/api/diagnostics/cpu/${canceledId}/download`)).status(), 404);
    await page.getByRole('button', { name: 'Delete CPU capture', exact: true }).click();
    await page.getByText('No CPU captures yet.', { exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log('Passed: real five-minute startup capture, baseline phases, live indicator, navigation, ZIP summaries, idle polling stop, manual cancellation, deletion and phone layouts.');
} finally { await browser.close(); await rm(scratch, { recursive: true, force: true }); }
