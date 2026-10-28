const assert = require('node:assert/strict');
const fs = require('node:fs');

(async () => {
    const source = fs.readFileSync(process.argv[2], 'utf8');
    const { PointerVelocity, beginThrow, advanceThrow } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
    const bounds = { left: 0, top: 0, right: 1400, bottom: 900 };
    const slow = new PointerVelocity();
    slow.add(0, 0, 0); slow.add(8, 0, 70);
    assert.equal(beginThrow(8, 0, slow.release(8, 0, 90), 140, bounds), null, 'gentle releases stay in place');
    const held = new PointerVelocity();
    held.add(0, 0, 0); held.add(100, -150, 50);
    assert.deepEqual(held.release(100, -150, 250), { x: 0, y: 0 }, 'holding after a quick drag discards old momentum');
    const flick = new PointerVelocity();
    flick.add(200, 500, 0); flick.add(240, 450, 40);
    const velocity = flick.release(260, 420, 60);
    assert.equal(velocity.x, 1000); assert.ok(velocity.y < -1300);
    const flight = beginThrow(500, 500, velocity, 140, bounds);
    assert.ok(Math.hypot(flight.vx, flight.vy) <= 1500.0001, 'fast flicks have bounded speed');
    let apex = flight.y, falling = false, landed = false;
    for (let i = 0; i < 240 && !landed; i++) {
        landed = advanceThrow(flight, 1 / 120, bounds);
        apex = Math.min(apex, flight.y); falling ||= flight.vy > 0;
        assert.ok(flight.x >= bounds.left && flight.x <= bounds.right);
        assert.ok(flight.y >= bounds.top && flight.y <= bounds.bottom);
    }
    assert.ok(apex < 400 && falling && landed, 'upward flick rises, turns under gravity and lands');
    assert.equal(flight.y, 605, 'the virtual floor lands a little below release');
    const left = beginThrow(800, 250, { x: -700, y: -100 }, 140, bounds);
    advanceThrow(left, .05, bounds);
    assert.ok(left.x < 800, 'leftward throws continue left');
    const at30 = beginThrow(400, 450, { x: 500, y: -750 }, 140, bounds);
    const at120 = { ...at30 };
    for (let i = 0; i < 12; i++) advanceThrow(at30, 1 / 30, bounds);
    for (let i = 0; i < 48; i++) advanceThrow(at120, 1 / 120, bounds);
    assert.ok(Math.abs(at30.x - at120.x) < .001 && Math.abs(at30.y - at120.y) < .001, 'trajectory does not depend on display frame rate');
    const wall = beginThrow(1395, 200, { x: 1000, y: -50 }, 140, bounds);
    advanceThrow(wall, .05, bounds); assert.equal(wall.x, 1400); assert.ok(wall.vx < 0, 'screen edge gives a small inward bounce');
    const ceiling = beginThrow(500, 1, { x: 0, y: -1000 }, 140, bounds);
    advanceThrow(ceiling, .05, bounds); assert.equal(ceiling.y, 0); assert.ok(ceiling.vy >= 0);
    assert.equal(advanceThrow(ceiling, .05, { left: 0, top: 0, right: 0, bottom: 0 }), true, 'a shrunken viewport settles safely');
    console.log('Pet throw sampling, gravity, direction, frame-rate parity and viewport bounds pass');
})().catch(error => { console.error(error); process.exitCode = 1; });
