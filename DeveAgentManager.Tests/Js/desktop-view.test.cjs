const assert = require('node:assert/strict');
const fs = require('node:fs');

(async () => {
    const source = fs.readFileSync(process.argv[2], 'utf8');
    const viewer = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
    const events = new Map(), timers = new Map(), revoked = [], reports = [];
    let timerId = 0, blobId = 0, requests = 0, failure = false, decodeFailure = false;
    global.document = { hidden: false, baseURI: 'https://manager.example/',
        addEventListener: (name, fn) => events.set(name, fn),
        removeEventListener: name => events.delete(name) };
    global.setTimeout = fn => { timers.set(++timerId, fn); return timerId; };
    global.clearTimeout = id => timers.delete(id);
    URL.createObjectURL = () => 'blob:frame-' + (++blobId);
    URL.revokeObjectURL = url => revoked.push(url);
    global.fetch = async (url, options) => {
        requests++;
        assert.equal(String(url), 'https://manager.example/api/frame');
        assert.equal(options.credentials, 'same-origin');
        assert.equal(options.cache, 'no-store');
        return { ok: !failure, headers: new Map([['Content-Type', 'image/jpeg'], ['X-DAM-Captured-At', new Date().toISOString()]]),
            json: async () => ({ detail: 'Desktop locked' }), blob: async () => new Blob(['frame']) };
    };
    const img = { src: '', decode: async () => { if (decodeFailure) throw new Error('decode'); }, removeAttribute: () => { img.src = ''; } };
    const host = { querySelector: () => img };
    const dotnet = { invokeMethodAsync: async (...args) => reports.push(args) };
    const flush = () => new Promise(setImmediate);

    viewer.start(host, dotnet, 'api/frame');
    await flush();
    assert.equal(requests, 1);
    assert.equal(timers.size, 0, 'snapshot mode has no timer');
    viewer.setLive(host, true, 2);
    await flush();
    assert.equal(requests, 2);
    assert.equal(timers.size, 1);
    document.hidden = true;
    events.get('visibilitychange')();
    await flush();
    assert.equal(timers.size, 0, 'hidden viewer stops polling');
    await viewer.refresh(host);
    assert.equal(requests, 2, 'manual refresh also respects hidden state');
    document.hidden = false;
    events.get('visibilitychange')();
    await flush();
    assert.equal(requests, 3, 'visible live viewer resumes');
    viewer.setLive(host, false, 2);
    assert.equal(timers.size, 0);

    const last = img.src;
    failure = true;
    await viewer.refresh(host);
    assert.equal(img.src, last, 'capture error retains last frame');
    assert.equal(reports.at(-1)[2], 'Desktop locked');
    failure = false;
    decodeFailure = true;
    await viewer.refresh(host);
    assert.equal(img.src, last, 'unreadable new frame restores last frame');
    assert.equal(reports.at(-1)[2], 'The desktop returned an unreadable image.');
    viewer.stop(host);
    assert.equal(events.size, 0);
    assert.equal(timers.size, 0);
    assert(revoked.includes(last), 'navigation releases the last blob');
    const after = requests;
    await viewer.refresh(host);
    assert.equal(requests, after, 'disposed viewer never captures again');
    console.log('Desktop viewer visibility, navigation, polling and frame-error regressions pass');
})().catch(error => { console.error(error); process.exitCode = 1; });
