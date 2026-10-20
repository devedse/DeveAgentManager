const { test } = require('node:test');
const assert = require('node:assert/strict');
process.env.TZ = 'Europe/Amsterdam';
const { localParts, toUtc } = require(process.argv[2] || '../../DeveAgentManager/wwwroot/js/schedule-time.js');
test('local reset uses DST-aware device timezone across midnight', () => {
 assert.deepEqual(localParts('2026-09-30T22:30:00Z'), {date:'2026-10-01',time:'00:30',zone:'Europe/Amsterdam',offset:'+02:00'});
 assert.deepEqual(localParts('2026-10-25T01:00:00Z'), {date:'2026-10-25',time:'02:00',zone:'Europe/Amsterdam',offset:'+01:00'});
 assert.deepEqual(localParts('2026-10-25T00:00:00Z'), {date:'2026-10-25',time:'02:00',zone:'Europe/Amsterdam',offset:'+02:00'});
 assert.equal(toUtc('2026-10-01','00:30').utc,'2026-09-30T22:30:00.000Z');
 assert.equal(toUtc('2026-12-01','00:30').utc,'2026-11-30T23:30:00.000Z');
});
test('rejects nonexistent and repeated DST wall-clock times', () => {
 assert.match(toUtc('2026-03-29','02:30').error,/does not exist/);
 assert.match(toUtc('2026-10-25','02:30').error,/occurs twice/);
 assert.equal(toUtc('2026-10-25','03:30').utc,'2026-10-25T02:30:00.000Z');
});
test('rejects invalid picker text', () => {
 assert.ok(toUtc('2026-02-30','10:00').error);
 assert.ok(toUtc('2026-10-01','25:00').error);
 assert.ok(toUtc('x','y').error);
});
