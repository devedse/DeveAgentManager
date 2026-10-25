// Run against a disposable Manager: My settings → Models shows the Manager's default model and effort, saves the
// user's own and goes back to the Manager's default. Loading states use deliberately slow and failing responses.
import assert from 'node:assert/strict';
const playwright = (await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright'));
const browser = await playwright.chromium.launch();
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5099';
const password = process.env.DAM_TEST_PASSWORD ?? 'test-only-password';
const endpoint = '/api/me/settings/models';
const headers = { 'X-DAM-Request': '1', Origin: base };
try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('[name=username]').fill('admin');
    await page.locator('[name=password]').fill(password);
    await Promise.all([page.waitForURL(url => url.pathname !== '/login'), page.locator('button[type=submit]').click()]);
    if (await page.locator('[name=currentPassword]').isVisible()) {
        await page.locator('[name=currentPassword]').fill(password);
        await page.locator('[name=newPassword]').fill('test-only-model-defaults-new-password');
        await page.locator('[name=confirmPassword]').fill('test-only-model-defaults-new-password');
        await Promise.all([page.waitForURL(base + '/'), page.locator('button[type=submit]').click()]);
    }

    // The Manager's default, as a settings manager sets it for everyone. Put back afterwards for the next regressions.
    const profile = await (await context.request.get(base + '/api/settings/profile')).json();
    const original = structuredClone(profile);
    profile.harnesses = { ...profile.harnesses, codex: { ...(profile.harnesses?.codex ?? {}), defaultModel: 'gpt-6.1-sol', defaultThinkingLevel: 'xhigh' } };
    assert.equal((await context.request.put(base + '/api/settings/profile', { data: profile, headers })).status(), 200);
    const mine = async () => (await (await context.request.get(base + endpoint)).json()).find(m => m.harnessId === 'codex');

    const section = page.locator('[data-section=model-defaults]');
    const codex = section.locator('[data-harness=codex]');
    const model = codex.locator('input').first();
    const effort = codex.locator('.model-default-effort');
    const effortInput = effort.locator('input').first();
    const save = section.getByRole('button', { name: 'Save', exact: true });
    const open = async () => {
        await page.goto(base + '/settings');
        await page.getByRole('tab', { name: 'Models' }).click();
        await codex.waitFor();
    };
    const persist = async () => {
        const response = page.waitForResponse(r => new URL(r.url()).pathname === endpoint && r.request().method() === 'PUT');
        await save.click();
        assert.equal((await response).status(), 200);
        await page.getByText('Saved. Your agents use it from the next conversation they start.', { exact: true }).last().waitFor();
    };

    await open();
    assert.equal(await model.getAttribute('placeholder'), 'Manager default');
    assert.equal(await effortInput.getAttribute('placeholder'), 'Manager default');
    assert.deepEqual([await model.inputValue(), await effortInput.inputValue()], ['', '']);
    await codex.getByText('Manager default: gpt-6.1-sol · xhigh.', { exact: true }).waitFor();
    assert.equal(await codex.getByRole('button', { name: 'Use Manager default' }).count(), 0);
    console.log("The Manager's default is shown until the user chooses their own.");

    await effortInput.click();
    await page.getByRole('option', { name: 'high', exact: true }).click();
    await codex.getByRole('button', { name: 'Use Manager default' }).waitFor();
    await persist();
    assert.deepEqual([(await mine()).model, (await mine()).effort], [null, 'high']);
    await page.reload();
    await page.getByRole('tab', { name: 'Models' }).click();
    await codex.waitFor();
    assert.equal(await effortInput.inputValue(), 'high');
    await codex.getByText('Manager default: gpt-6.1-sol · xhigh.', { exact: true }).waitFor();
    console.log('An own effort is saved, kept after reload, and the Manager default stays visible.');

    await codex.getByRole('button', { name: 'Use Manager default' }).click();
    await persist();
    assert.deepEqual([(await mine()).model, (await mine()).effort], [null, null]);
    assert.equal(await effortInput.inputValue(), '');
    assert.equal(await codex.getByRole('button', { name: 'Use Manager default' }).count(), 0);
    console.log("Use Manager default goes back to the Manager's default.");

    let slow = true, failLoad = false;
    await context.route('**' + endpoint, async route => {
        if (route.request().method() === 'GET' && slow) await new Promise(resolve => setTimeout(resolve, 1800));
        if (route.request().method() === 'GET' && failLoad)
            return route.fulfill({ status: 500, json: { title: 'Test-only models failure', detail: 'Test-only models failure' } });
        return route.continue();
    });
    await page.goto(base + '/settings');
    await page.getByRole('tab', { name: 'Models' }).click();
    await section.locator('[aria-busy=true]').waitFor();
    assert.equal(await codex.count(), 0, 'nothing is shown as the default before it loads');
    await codex.waitFor();
    slow = false; failLoad = true;
    await page.reload();
    await page.getByRole('tab', { name: 'Models' }).click();
    await section.getByText('Test-only models failure').first().waitFor();
    assert.equal(await codex.count(), 0);
    failLoad = false;
    await section.getByRole('button', { name: 'Retry', exact: true }).click();
    await codex.waitFor();
    console.log('Slow and failed loads show loading and Retry, not a default.');

    for (const theme of ['light', 'dark']) {
        await page.addInitScript(value => localStorage.setItem('dam-theme', value), theme);
        for (const width of [390, 320]) {
            await page.setViewportSize({ width, height: 850 });
            await open();
            const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
            assert.ok(overflow <= 0, `no horizontal scroll at ${width}px (${theme}): ${overflow}px`);
            if (process.env.DAM_SCREENSHOTS) await page.screenshot({ path: `${process.env.DAM_SCREENSHOTS}/models-${theme}-${width}.png`, fullPage: true });
        }
    }
    console.log('Fits at 390 and 320 pixels in light and dark.');
    assert.deepEqual(errors, []);
    assert.equal((await context.request.put(base + '/api/settings/profile', { data: original, headers })).status(), 200);
} finally {
    await browser.close();
}
