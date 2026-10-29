import assert from 'node:assert/strict';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright') as typeof import('playwright');
const base = process.env.DAM_TEST_URL ?? 'http://127.0.0.1:5109';
const browser = await chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] });
declare global {
    interface Window {
        codexVoiceTest: { streams: MediaStream[]; peers: any[]; denied: boolean; hold: boolean; release: (() => void) | null; blocked: boolean; rejectReplacement: boolean };
    }
}
try {
    const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, hasTouch: true });
    await context.addInitScript(() => {
        const test = window.codexVoiceTest = { streams: [], peers: [], denied: false, hold: false, release: null, blocked: false, rejectReplacement: false };
        const original = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
        navigator.mediaDevices.getUserMedia = async constraints => {
            if (test.denied) throw new DOMException('Test-only denial', 'NotAllowedError');
            if (test.hold) { test.hold = false; await new Promise<void>(resolve => { test.release = resolve; }); }
            const audio = constraints.audio as MediaTrackConstraints;
            const id = (audio?.deviceId as ConstrainDOMStringParameters)?.exact || 'test-mic-one';
            if (id === 'test-missing') throw new DOMException('Test-only missing device', 'NotFoundError');
            const stream = await original({ audio: true });
            const track = stream.getAudioTracks()[0];
            const settings = track.getSettings.bind(track);
            track.getSettings = () => ({ ...settings(), deviceId: String(id) });
            test.streams.push(stream); return stream;
        };
        navigator.mediaDevices.enumerateDevices = async () => [
            { deviceId: 'test-mic-one', kind: 'audioinput', label: 'Microphone one', groupId: 'test-one' },
            { deviceId: 'test-mic-two', kind: 'audioinput', label: 'Microphone two', groupId: 'test-two' },
        ] as MediaDeviceInfo[];
        // Real browser microphone tracks with deterministic signaling: lifecycle failures need no upstream account.
        window.RTCPeerConnection = class {
            localDescription = { type: 'offer', sdp: 'test-only offer' };
            connectionState = 'new';
            onconnectionstatechange = null;
            ontrack = null;
            channel = { onopen: null, onmessage: null, readyState: 'connecting' };
            sender = { track: null, async replaceTrack(track) {
                if (test.rejectReplacement) throw new Error('Test-only replacement failure');
                this.track = track;
            } };
            constructor() { test.peers.push(this); }
            addTrack(track) { this.sender.track = track; return this.sender; }
            createDataChannel() { return this.channel; }
            async createOffer() { return this.localDescription; }
            async setLocalDescription() { }
            async setRemoteDescription() { this.connectionState = 'connected'; this.channel.readyState = 'open'; this.channel.onopen?.(); }
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
    assert.equal((await context.request.put(base + '/api/me/settings/codex-voice', { data: { terminalMode: false }, headers: { 'X-DAM-Request': '1', Origin: base } })).ok(), true);
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
    const terminalInput: unknown[] = [];
    await context.routeWebSocket('**/ws/terminal?**', ws => {
        ws.send(JSON.stringify({ type: 'ready', tty: '/dev/pts/99' }));
        ws.onMessage(raw => { if (typeof raw !== 'string') terminalInput.push(raw); });
    });
    let reject = false;
    const messages: { type: string; sdp?: string; mode?: string }[] = [];
    let requestTerminalVoice: (() => void) | null = null;
    let nativeControls: ((value: object) => void) | null = null;
    await context.routeWebSocket('**/ws/agents/*/voice', ws => ws.onMessage(raw => {
        const message = JSON.parse(String(raw)); messages.push(message);
        if (message.type === 'listen') {
            requestTerminalVoice = () => ws.send(JSON.stringify({ type: 'requested' }));
            ws.send(JSON.stringify({ type: 'ready' }));
        }
        if (message.type === 'start') ws.send(JSON.stringify(reject
            ? { type: 'error', message: 'Voice is already connected in another tab. End it there first.' }
            : { type: 'answer', sdp: 'test-only answer' }));
        if (message.type === 'start' && message.mode === 'terminal') nativeControls = value => ws.send(JSON.stringify(value));
        if (message.type === 'connected') ws.send(JSON.stringify({ type: 'devices' }));
        if (message.type === 'devicesOpened' && message.available) ws.send(JSON.stringify({ type: 'controls', microphoneMuted: false, speakerSuppressed: false }));
        if (message.type === 'controlsApplied') ws.send(JSON.stringify({ type: 'active' }));
    }));
    const navigate = async () => {
        await page.goto(base + `/agents/${id}`);
        await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor({ timeout: 60000 });
    };
    const closedTracks = () => page.evaluate(() => window.codexVoiceTest.streams.every(s => s.getTracks().every(t => t.readyState === 'ended')));
    await navigate();
    assert.equal(messages.length, 0, 'viewing an agent does not start voice');
    await page.evaluate(() => {
        window.codexVoiceTest.denied = true;
        localStorage.setItem('dam-voice-microphone', 'test-missing');
    });
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Microphone access was denied. Allow it in your browser and retry.', { exact: true }).waitFor();
    assert.equal(messages.length, 0, 'denied permission does not create a session');
    await page.evaluate(() => { window.codexVoiceTest.denied = false; });
    await page.keyboard.press('F8');
    await page.getByText('Voice connected', { exact: true }).waitFor();
    assert.equal(messages.filter(m => m.type === 'start').length, 1, 'F8 starts exactly one call');
    const transcript = page.getByRole('log', { name: 'Spoken conversation' });
    const emitCaptions = (events: unknown[]) => page.evaluate(items => {
        const channel = window.codexVoiceTest.peers.at(-1).channel;
        for (const event of items) channel.onmessage({ data: JSON.stringify(event) });
    }, events);
    await emitCaptions([
        { type: 'turn.created', turn: { id: 'ordinary-user', role: 'user', transcript: 'Hello,' } },
        { type: 'turn.created', turn: { id: 'ordinary-assistant', role: 'assistant', transcript: 'I hear' } },
        { type: 'turn.delta', turn_id: 'ordinary-user', delta: ' how are you?' },
        { type: 'turn.delta', turn_id: 'ordinary-assistant', delta: ' you clearly.' },
        { type: 'input_transcript.added', item: { id: 'same-audio', text: ' how are you?' } },
    ]);
    await transcript.getByText('Hello, how are you?', { exact: true }).waitFor();
    await transcript.getByText('I hear you clearly.', { exact: true }).waitFor();
    assert.equal(await transcript.locator('.terminal-voice-transcript-line').count(), 2, 'duplex fragments form two captions without duplicate audio items');
    await emitCaptions([
        { type: 'turn.done', turn: { id: 'ordinary-user', role: 'user', transcript: 'Hello, how are you doing?' } },
        { type: 'turn.done', turn: { id: 'ordinary-assistant', role: 'assistant', transcript: 'I hear you clearly.' } },
        { type: 'turn.delta', turn_id: 'ordinary-user', delta: ' unwanted late text' },
        { type: 'turn.created', turn: { id: 'escaped-text', role: 'user', transcript: '<img src=x onerror=alert(1)>' } },
    ]);
    await transcript.getByText('Hello, how are you doing?', { exact: true }).waitFor();
    await transcript.getByText('<img src=x onerror=alert(1)>', { exact: true }).waitFor();
    assert.equal(await transcript.locator('img').count(), 0, 'spoken content is text, never HTML');
    assert.equal(await transcript.getByText(/unwanted late text/).count(), 0);
    assert.deepEqual(terminalInput, [], 'captions never become terminal input');
    assert.equal(messages.filter(m => m.type === 'start').length, 1, 'ordinary conversation does not start another coding or voice request');
    await emitCaptions(Array.from({ length: 30 }, (_, i) => ({ type: 'turn.done', turn: {
        id: `scroll-${i}`, role: i % 2 ? 'assistant' : 'user', transcript: `Spoken sentence ${i} with enough words to fill a transcript line.`,
    } })));
    await transcript.getByText('Spoken sentence 29 with enough words to fill a transcript line.', { exact: true }).waitFor();
    await page.waitForFunction(() => {
        const log = document.querySelector('.terminal-voice-transcript-lines');
        return log.scrollHeight - log.scrollTop - log.clientHeight <= 24;
    });
    await transcript.evaluate(el => { el.scrollTop = 0; el.dispatchEvent(new Event('scroll')); });
    await emitCaptions([{ type: 'turn.done', turn: { id: 'read-earlier', role: 'user', transcript: 'New words while reading earlier speech.' } }]);
    await transcript.getByText('New words while reading earlier speech.', { exact: true }).waitFor({ state: 'attached' });
    assert.equal(await transcript.evaluate(el => el.scrollTop), 0, 'new speech does not move a user reading earlier captions');
    await transcript.evaluate(el => { el.scrollTop = el.scrollHeight; el.dispatchEvent(new Event('scroll')); });
    await page.getByRole('button', { name: 'Close voice transcript', exact: true }).click();
    await transcript.waitFor({ state: 'hidden' });
    await page.getByText('Voice connected', { exact: true }).waitFor();
    await emitCaptions([{ type: 'turn.done', turn: { id: 'closed-caption', role: 'user', transcript: 'Speech continues while the transcript is closed.' } }]);
    assert.equal(await transcript.isVisible(), false, 'new words do not reopen a dismissed transcript');
    await page.getByRole('button', { name: 'Show transcript', exact: true }).click();
    await transcript.waitFor();
    await transcript.getByText('Speech continues while the transcript is closed.', { exact: true }).waitFor();
    assert.equal(messages.filter(m => m.type === 'stop').length, 0, 'closing captions leaves voice connected');
    const microphone = page.getByRole('combobox', { name: 'Microphone', exact: true });
    await microphone.waitFor();
    assert.equal(await microphone.inputValue(), 'test-mic-one', 'a missing preferred device falls back to the available microphone');
    await page.waitForFunction(() => Number(document.querySelector('[aria-label="Microphone input level"]')?.getAttribute('aria-valuenow')) > 0);
    await page.getByRole('button', { name: 'Mute microphone', exact: true }).click();
    await page.getByText('Microphone muted', { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => window.codexVoiceTest.streams.at(-1).getAudioTracks()[0].enabled), false);
    await page.waitForFunction(() => document.querySelector('[aria-label="Microphone input level"]')?.getAttribute('aria-valuenow') === '0');
    await microphone.selectOption('test-mic-two');
    await page.waitForFunction(() => window.codexVoiceTest.peers.at(-1).sender.track === window.codexVoiceTest.streams.at(-1).getAudioTracks()[0]);
    assert.equal(await page.evaluate(() => window.codexVoiceTest.streams[0].getAudioTracks()[0].readyState), 'ended', 'changing microphones releases the previous input');
    assert.equal(await page.evaluate(() => window.codexVoiceTest.peers.at(-1).sender.track.enabled), false, 'a microphone switch preserves mute');
    assert.equal(messages.filter(m => m.type === 'start').length, 1, 'changing microphones keeps the existing call');
    assert.equal(await page.evaluate(() => localStorage.getItem('dam-voice-microphone')), 'test-mic-two', 'the chosen microphone is remembered');
    await page.evaluate(() => { window.codexVoiceTest.rejectReplacement = true; });
    await microphone.selectOption('test-mic-one');
    await page.getByText('That microphone could not open. Your previous microphone is still selected.', { exact: true }).waitFor();
    assert.equal(await microphone.inputValue(), 'test-mic-two', 'failed replacement retains the selected microphone');
    assert.equal(await page.evaluate(() => window.codexVoiceTest.peers.at(-1).sender.track.readyState), 'live', 'failed replacement leaves the active input live');
    assert.equal(await page.evaluate(() => window.codexVoiceTest.streams.at(-1).getAudioTracks()[0].readyState), 'ended', 'failed replacement releases its unused capture');
    await page.evaluate(() => { window.codexVoiceTest.rejectReplacement = false; });
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
        for (const { width, height } of [{ width: 320, height: 844 }, { width: 390, height: 844 }, { width: 740, height: 300 }, { width: 1400, height: 900 }]) {
            await page.setViewportSize({ width, height });
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${theme} ${width}: controls fit`);
            assert.ok(await transcript.evaluate(el => el.clientHeight <= 4 * parseFloat(getComputedStyle(el).lineHeight) + 12), 'the transcript is at most four text lines tall');
            await page.getByRole('button', { name: 'Help about Codex voice', exact: true }).click();
            const help = page.locator('.help-tooltip-centered.mud-popover-open');
            await help.waitFor();
            await page.waitForTimeout(300);
            const bounds = await help.boundingBox();
            assert.ok(bounds && bounds.x >= 15 && bounds.x + bounds.width <= width - 15
                && bounds.y >= 15 && bounds.y + bounds.height <= height - 15, `${theme} ${width}x${height}: help fits the viewport`);
            assert.ok(bounds.width >= Math.min(360, width - 32) - 1, 'help keeps a readable width beside the right-edge icon');
            const content = help.locator('.help-tip-content');
            const beginning = await content.boundingBox();
            assert.ok(beginning && beginning.y >= bounds.y, 'the beginning of long help is visible');
            await help.evaluate(el => { el.scrollTop = el.scrollHeight; });
            const end = await content.locator('p').last().boundingBox();
            assert.ok(end && end.y + end.height <= bounds.y + bounds.height, 'the last help paragraph is reachable by scrolling');
            await page.screenshot({ path: `/tmp/dam-codex-voice-${theme}-${width}.png` });
            await page.keyboard.press('Escape');
            await help.waitFor({ state: 'hidden' });
        }
    }
    // Safari's keyboard covers the layout viewport; reuse the terminal's measured visible area.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(300);
    await page.evaluate(() => {
        document.documentElement.style.setProperty('--dam-vv-top', '120px');
        document.documentElement.style.setProperty('--dam-vv-height', '300px');
    });
    await page.getByRole('button', { name: 'Help about Codex voice', exact: true }).click();
    const coveredHelp = page.locator('.help-tooltip-centered.mud-popover-open');
    await coveredHelp.waitFor();
    await page.waitForTimeout(300);
    const coveredBounds = await coveredHelp.boundingBox();
    assert.ok(coveredBounds && coveredBounds.y >= 135 && coveredBounds.y + coveredBounds.height <= 405,
        'help fits above a keyboard that covers the layout viewport');
    await coveredHelp.locator('.help-tip-content').tap();
    await coveredHelp.waitFor({ state: 'hidden' });
    await page.evaluate(() => {
        document.documentElement.style.removeProperty('--dam-vv-top');
        document.documentElement.style.removeProperty('--dam-vv-height');
    });
    await page.keyboard.press('F8');
    await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor();
    assert.ok(await transcript.locator('.terminal-voice-transcript-line').count() > 0, 'captions remain readable after ending voice');
    await page.evaluate(() => window.codexVoiceTest.peers[0].channel.onmessage({ data: JSON.stringify({ type: 'turn.created',
        turn: { id: 'stale', role: 'user', transcript: 'Stale callback after ending the call' } }) }));
    assert.equal(await transcript.getByText('Stale callback after ending the call', { exact: true }).count(), 0);
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
    assert.equal(await microphone.inputValue(), 'test-mic-two', 'the next call uses the chosen microphone');
    await page.evaluate(() => { window.codexVoiceTest.hold = true; window.codexVoiceTest.release = null; });
    await microphone.selectOption('test-mic-one');
    await page.waitForFunction(() => window.codexVoiceTest.release !== null);
    await page.getByRole('button', { name: 'End voice', exact: true }).click();
    await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor();
    await page.evaluate(() => window.codexVoiceTest.release());
    await page.waitForTimeout(300);
    assert.equal(await closedTracks(), true, 'ending voice during an input switch also releases the late replacement');

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
    agent.harnessId = 'codex';
    await page.goto(base + '/settings?tab=voice');
    const terminalToggle = page.getByLabel('Use terminal voice', { exact: true });
    await terminalToggle.waitFor();
    assert.equal(await terminalToggle.isChecked(), false, 'browser mode is the initial preference');
    await terminalToggle.check();
    await page.waitForFunction(async () => (await (await fetch('/api/me/settings/codex-voice')).json()).terminalMode === true);
    await navigate();
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Terminal voice connected', { exact: true }).waitFor();
    assert.equal(messages.filter(m => m.type === 'start').at(-1).mode, 'terminal', 'the saved preference selects native signaling');
    assert.equal(await page.getByRole('button', { name: 'Mute microphone', exact: true }).count(), 0, 'native mode uses Codex terminal mute controls');
    await emitCaptions([{ type: 'turn.created', turn: { id: 'native-turn', role: 'user', transcript: 'Native speech stays in the native terminal.' } }]);
    assert.equal(await page.locator('.terminal-voice-transcript').isVisible(), false, 'native mode does not duplicate the terminal transcript');
    nativeControls({ type: 'controls', microphoneMuted: true, speakerSuppressed: true });
    await page.waitForFunction(() => window.codexVoiceTest.streams.at(-1).getAudioTracks()[0].enabled === false);
    assert.equal(await page.locator('.terminal-voice-container audio').evaluate((el: HTMLAudioElement) => el.muted), true, 'native speaker suppression reaches browser playback');
    nativeControls({ type: 'controls', microphoneMuted: false, speakerSuppressed: false });
    await page.waitForFunction(() => window.codexVoiceTest.streams.at(-1).getAudioTracks()[0].enabled === true);
    requestTerminalVoice = null;
    const listenCount = messages.filter(m => m.type === 'listen').length;
    await page.getByRole('button', { name: 'End voice', exact: true }).click();
    await page.getByRole('button', { name: 'Start voice', exact: true }).waitFor();
    assert.equal(await closedTracks(), true, 'native mode releases its browser microphone');
    await page.waitForFunction(() => window.codexVoiceTest.streams.every(s => s.getTracks().every(t => t.readyState === 'ended')));
    await new Promise<void>((resolve, reject) => {
        const started = Date.now();
        const poll = setInterval(() => {
            if (requestTerminalVoice && messages.filter(m => m.type === 'listen').length > listenCount) { clearInterval(poll); resolve(); }
            else if (Date.now() - started > 10000) { clearInterval(poll); reject(new Error('Terminal listener did not rearm')); }
        }, 25);
    });
    assert.equal(await closedTracks(), true, 'the idle terminal listener does not capture audio');
    const startsBeforeRequest = messages.filter(m => m.type === 'start').length;
    requestTerminalVoice!();
    await page.getByText('Terminal voice connected', { exact: true }).waitFor();
    assert.equal(messages.filter(m => m.type === 'start').length, startsBeforeRequest + 1, 'a terminal request starts exactly one call');
    requestTerminalVoice = null;
    await page.getByRole('button', { name: 'End voice', exact: true }).click();
    assert.equal(await closedTracks(), true, 'terminal-requested voice releases the microphone on end');
    await page.evaluate(() => { window.codexVoiceTest.denied = true; });
    await new Promise<void>((resolve, reject) => {
        const started = Date.now();
        const poll = setInterval(() => {
            if (requestTerminalVoice) { clearInterval(poll); resolve(); }
            else if (Date.now() - started > 10000) { clearInterval(poll); reject(new Error('Terminal listener did not rearm')); }
        }, 25);
    });
    const startsBeforeDenial = messages.filter(m => m.type === 'start').length;
    requestTerminalVoice!();
    await page.getByText('Microphone access was denied. Allow it in your browser and retry.', { exact: true }).waitFor();
    assert.equal(await closedTracks(), true, 'denied terminal startup leaves no live microphone');
    assert.equal(messages.filter(m => m.type === 'start').length, startsBeforeDenial, 'denial does not submit an audio offer');
    await page.evaluate(() => { window.codexVoiceTest.denied = false; });


    const restored = await context.request.put(base + '/api/me/settings/codex-voice', { data: { terminalMode: false }, headers: { 'X-DAM-Request': '1', Origin: base } });
    assert.equal(restored.ok(), true);
    await page.getByRole('button', { name: 'Start voice', exact: true }).click();
    await page.getByText('Voice connected', { exact: true }).waitFor();
    assert.equal(messages.filter(m => m.type === 'start').at(-1).mode, undefined, 'the next call reads a changed preference without reloading');
    await page.getByRole('button', { name: 'End voice', exact: true }).click();
    assert.deepEqual(errors, []);
    console.log('PASS: four-line captions, close/reopen, streaming, safe scrolling, native mode preference and controls, next-call mode changes, microphone lifecycle, ownership and phone layouts');
} finally { await browser.close(); }
