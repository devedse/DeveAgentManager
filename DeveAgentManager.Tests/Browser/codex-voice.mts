import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5109';
const browser = await chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
declare global {
    interface Window {
        codexVoiceTest: { streams: MediaStream[]; peers: any[]; denied: boolean; hold: boolean; release: (() => void) | null; blocked: boolean };
    }
}
try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
    await context.addInitScript(() => {
        const test = window.codexVoiceTest = { streams: [], peers: [], denied: false, hold: false, release: null, blocked: false };
        const original = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
        navigator.mediaDevices.getUserMedia = async constraints => {
            if (test.denied) throw new DOMException('Test-only denial', 'NotAllowedError');
            if (test.hold) { test.hold = false; await new Promise<void>(resolve => { test.release = resolve; }); }
            const stream = await original(constraints); test.streams.push(stream); return stream;
        };
        // Real browser microphone tracks with deterministic signaling: lifecycle failures need no upstream account.
        window.RTCPeerConnection = class {
            localDescription = { type: 'offer', sdp: 'test-only offer' };
            connectionState = 'new';
            onconnectionstatechange = null;
            ontrack = null;
            channel = { onopen: null, onmessage: null };
            constructor() { test.peers.push(this); }
            addTrack() { }
            createDataChannel() { return this.channel; }
            async createOffer() { return this.localDescription; }
            async setLocalDescription() { }
            async setRemoteDescription() { this.connectionState = 'connected'; this.channel.onopen?.(); }
            close() { this.connectionState = 'closed'; }
        } as unknown as typeof RTCPeerConnection;
        HTMLMediaElement.prototype.play = async function () {
            if (test.blocked) throw new DOMException('Test-only autoplay block', 'NotAllowedError');
        };
    });
    const page = await context.newPage();
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/login');
    await page.locator('input[name="username"]').fill('admin');
    await page.locator('input[name="password"]').fill(process.env.DAM_TEST_PASSWORD ?? 'preview-test-only');
    await Promise.all([page.waitForURL(base + '/'), page.locator('button[type="submit"]').click()]);
    const me = await (await context.request.get(base + '/api/me')).json();
    const info = await (await context.request.get(base + '/api/info')).json();
    const id = '00000000-0000-0000-0000-000000000111';
    const runnerId = '00000000-0000-0000-0000-000000000112';
    const now = new Date().toISOString();
    const agent = { id, runnerId, ownerId: me.id, ownerName: 'Test user', access: 'Owner', runnerName: 'test-runner', runnerState: 'Ready',
        runnerHarnessIds: ['codex'], runnerToolIds: [], name: 'voice-test', harnessId: 'codex', workingDirectory: '/work/example',
        state: 'Running', createdAt: now, updatedAt: now, canUseRunner: true };
    await context.route('**/api/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/me' || path.startsWith('/api/me/') || path === '/api/build' || path === '/api/diagnostics/cpu') return route.continue();
        const body = path === '/api/info' ? info : path === '/api/agents' ? [agent] : path === `/api/agents/${id}` ? agent
            : path === `/api/runners/${runnerId}` ? { id: runnerId, name: 'test-runner', state: 'Ready', agents: [agent] }
            : path.endsWith('/session') ? null : path.endsWith('/timezone') ? { defaultTimeZoneId: 'UTC', overrideTimeZoneId: null } : [];
        return route.fulfill({ json: body });
    });
    await context.routeWebSocket('**/ws/terminal?**', ws => ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' })));
    let reject = false;
    const messages: { type: string; sdp?: string }[] = [];
    await context.routeWebSocket('**/ws/agents/*/voice', ws => ws.onMessage(raw => {
        const message = JSON.parse(String(raw)); messages.push(message);
        if (message.type === 'start') ws.send(JSON.stringify(reject
            ? { type: 'error', message: 'Voice is already connected in another tab. End it there first.' }
            : { type: 'answer', sdp: 'test-only answer' }));
    }));
    const navigate = async () => {
        await page.goto(base + `/agents/${id}`);
        await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor({ timeout: 60000 });
    };
    const closedTracks = () => page.evaluate(() => window.codexVoiceTest.streams.every(s => s.getTracks().every(t => t.readyState === 'ended')));
    await navigate();
    assert.equal(messages.length, 0, 'viewing an agent does not start voice');
    await page.evaluate(() => { window.codexVoiceTest.denied = true; });
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Microphone access was denied. Allow it in your browser and retry.', { exact: true }).waitFor();
    assert.equal(messages.length, 0, 'denied permission does not create a session');
    await page.evaluate(() => { window.codexVoiceTest.denied = false; });
    await page.keyboard.press('F8');
    await page.getByText('Voice connected', { exact: true }).waitFor();
    assert.equal(messages.filter(m => m.type === 'start').length, 1, 'F8 starts exactly one call');
    await page.getByRole('button', { name: 'Mute microphone', exact: true }).click();
    await page.getByText('Microphone muted', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => window.codexVoiceTest.streams.at(-1).getAudioTracks()[0].enabled), false);
    await page.getByRole('button', { name: 'Unmute microphone', exact: true }).click();
    await page.getByText('Voice connected', { exact: true }).waitFor();
    await page.evaluate(() => {
        window.codexVoiceTest.blocked = true;
        window.codexVoiceTest.peers.at(-1).ontrack({ track: window.codexVoiceTest.streams.at(-1).getAudioTracks()[0] });
    });
    await page.getByRole('button', { name: 'Play audio', exact: true }).waitFor();
    await page.evaluate(() => { window.codexVoiceTest.blocked = false; });
    await page.getByRole('button', { name: 'Play audio', exact: true }).click();
    await page.getByRole('button', { name: 'Play audio', exact: true }).waitFor({ state: 'hidden' });
    for (const theme of ['dark', 'light']) {
        await page.evaluate(value => window.damTheme.set(value), theme);
        for (const width of [320, 390]) {
            await page.setViewportSize({ width, height: 844 });
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: controls fit`);
            await page.screenshot({ path: `/tmp/dam-codex-voice-${theme}-${width}.png` });
        }
    }
    await page.keyboard.press('F8');
    await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor();
    assert.equal(await closedTracks(), true, 'ending voice immediately stops microphone tracks');
    assert.equal(messages.filter(m => m.type === 'stop').length, 1);

    reject = true;
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Voice is already connected in another tab. End it there first.', { exact: true }).waitFor();
    assert.equal(await closedTracks(), true, 'competing-tab failure releases the microphone');
    reject = false;
    await page.evaluate(() => { window.codexVoiceTest.hold = true; });
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Allow microphone access…', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'End voice', exact: true }).click();
    const before = messages.filter(m => m.type === 'start').length;
    await page.evaluate(() => window.codexVoiceTest.release());
    await page.waitForTimeout(300);
    assert.equal(await closedTracks(), true, 'a late permission result cannot leak a microphone');
    assert.equal(messages.filter(m => m.type === 'start').length, before, 'a cancelled start is never sent later');

    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Voice connected', { exact: true }).waitFor();
    await page.evaluate(() => {
        Object.defineProperty(document, 'hidden', { configurable: true, value: true });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor();
    assert.equal(await closedTracks(), true, 'hiding the tab stops capture');
    await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); });
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Voice connected', { exact: true }).waitFor();
    await page.locator('.terminal-toolbar a[href="agents"]').click();
    await page.waitForURL(base + '/agents');
    assert.equal(await closedTracks(), true, 'client navigation stops capture');
    agent.access = 'Read';
    await page.goto(base + `/agents/${id}`);
    await page.locator('.terminal-readonly-banner').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Start voice', exact: true }).count(), 0, 'read-only users have no voice controls');
    agent.access = 'Owner'; agent.harnessId = 'pi';
    await page.goto(base + `/agents/${id}`);
    await page.locator('.terminal-frame').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Start voice', exact: true }).count(), 0, 'other harnesses have no Codex voice controls');
    assert.deepEqual(errors, []);
    console.log('PASS: voice permission, F8, mute, playback recovery, competing tabs, pending permission cancellation, navigation, hidden tabs, read-only access and phone themes');
} finally { await browser.close(); }
