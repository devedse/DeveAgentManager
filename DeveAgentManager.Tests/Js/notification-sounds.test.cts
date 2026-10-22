const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync(process.argv[2], 'utf8');
const storage = new Map();
function tab() {
    let count = 0;
    class AudioContext {
        state = 'running';
        destination = {};
        async resume() { }
        async decodeAudioData() { return {}; }
        createBufferSource() { return { connect: x => x, start: () => count++ }; }
        createGain() { return { gain: {}, connect() {} }; }
    }
    const sandbox = { window: { AudioContext }, document: { baseURI: 'https://example.test/', addEventListener() {} },
        navigator: { locks: { request: async (_name, fn) => fn() } }, URL,
        localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
        fetch: async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(0) }) };
    vm.runInNewContext(source, sandbox);
    return { api: (sandbox.window as any).damNotificationSounds, count: () => count };
}
(async () => {
    const first = tab(), a = first.api;
    assert.equal(a.init('user-one').limit, false);
    const idle = [{ id: 'agent', finished: null, limit: null }];
    await a.observe(idle);
    await a.set('limit', true);
    assert.equal(first.count(), 1, 'enabling previews the sound');
    await a.observe([{ id: 'agent', limit: 'rejection-one' }]);
    assert.equal(first.count(), 2);
    await a.observe([{ id: 'agent', limit: 'rejection-one' }]);
    assert.equal(first.count(), 2, 'polling the same limit stays silent');
    await a.observe([{ id: 'agent', finished: 'completion-one' }]);
    assert.equal(first.count(), 2, 'finished is independently disabled');
    await a.set('finished', true);
    await a.observe([{ id: 'agent', finished: 'completion-two' }]);
    assert.equal(first.count(), 4);
    await a.observe([{ id: 'agent', finished: 'completion-two' }, { id: 'newly-shared', limit: 'old-limit' }]);
    assert.equal(first.count(), 4, 'newly visible agents do not replay old statuses');
    const second = tab(), b = second.api;
    assert.equal(b.init('user-one').finished, true, 'preferences persist across tabs/reloads');
    await b.set('limit', false); // off
    await b.set('limit', true); // on: preview unlocks this tab
    await b.observe(idle);
    await a.observe([{ id: 'agent', limit: 'rejection-two' }]);
    const before = second.count();
    await b.observe([{ id: 'agent', limit: 'rejection-two' }]);
    assert.equal(second.count(), before, 'another tab already played the event');
    await b.set('limit', false); // Disabling in another tab must also silence this one.
    const countBeforeDisable = first.count();
    await a.observe([{ id: 'agent', limit: 'rejection-three' }]);
    assert.equal(first.count(), countBeforeDisable, 'disabling applies across tabs');
    const reload = tab();
    reload.api.init('user-one');
    await reload.api.observe([{ id: 'agent', limit: 'rejection-two' }]);
    assert.equal(reload.count(), 0, 'initial page load is silent');
    a.init('another-user');
    await a.observe(idle);
    const previous = first.count();
    await a.observe([{ id: 'agent', limit: 'another-limit', finished: 'three' }]);
    assert.equal(first.count(), previous, 'preferences belong to the signed-in user');
    console.log('Passed: opt-in previews, independent toggles, transitions, polling, reloads, newly shared agents, tab deduplication and per-user preferences.');
})().catch(error => { console.error(error); process.exitCode = 1; });
