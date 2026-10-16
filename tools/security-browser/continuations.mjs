// Runs the continuation UI regression against an isolated Manager and disposable test-only login.
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
const dll = resolve(process.env.DAM_DLL || '.validation/publish/DeveAgentManager.dll');
const scratch = mkdtempSync(join(tmpdir(), 'dam-continuation-browser-'));
const password = `test-only-${randomUUID()}`;
const base = 'http://127.0.0.1:19081';
const logs = [];
const manager = spawn(process.env.DOTNET || 'dotnet', [dll], {
    cwd: dirname(dll),
    env: { ...process.env, DATA_DIR: scratch, ADMIN_USERNAME: 'admin', ADMIN_PASSWORD: password,
        PROXMOX_URL: '', ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: base,
        PUBLIC_ORIGINS: '', ALLOW_LOCAL_ACCESS: 'true' },
    stdio: ['ignore', 'pipe', 'pipe']
});
manager.stdout.on('data', data => logs.push(data.toString()));
manager.stderr.on('data', data => logs.push(data.toString()));
const exited = new Promise(resolve => manager.once('exit', resolve));
try {
    let ready = false;
    for (let i = 0; i < 120; i++) {
        if (manager.exitCode !== null) throw new Error('Isolated Manager exited before starting');
        try { ready = (await fetch(base + '/login')).ok; } catch {}
        if (ready) break;
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    if (!ready) throw new Error('Isolated Manager did not start within 30 seconds');
    for (const script of ['auto-continue-dialog.mjs', 'agent-activity.mjs']) {
        const test = spawn(process.execPath, [resolve('DeveAgentManager.Tests/Browser', script)], {
            env: { ...process.env, DAM_TEST_URL: base, DAM_TEST_PASSWORD: password,
                PLAYWRIGHT_MODULE: resolve('tools/security-browser/node_modules/playwright/index.mjs') },
            stdio: 'inherit'
        });
        const code = await new Promise(resolve => test.once('exit', resolve));
        if (code !== 0) throw new Error(`Continuation browser regression ${script} failed (${code})`);
    }
} catch (error) {
    console.error(logs.join('').slice(-5000).replaceAll(password, '[test password]'));
    throw error;
} finally {
    if (manager.exitCode === null) manager.kill('SIGTERM');
    await exited;
    rmSync(scratch, { recursive: true, force: true });
}
