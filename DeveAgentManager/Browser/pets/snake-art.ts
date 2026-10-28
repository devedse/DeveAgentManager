import { StagedRenderer, RIG_STAGE, RIG_GROUND, RIG_INK, rigSmooth, rigBlink, rigEffects } from "./illustrated-rig.js";
import type { PetFrame } from "./types.js";
interface SnekFace { eyes: string; mouth: string; open: number; tongue: string | number; blink: boolean; gaze: { x: number; y: number }; lids?: number; wag?: number; }
interface SnekPlan { kind: string; p: any; face: SnekFace; lift: number; tilt: number; turn: number; breathe: number; shake: number; effects: Set<string>; coil?: any; coilAmount?: number; track?: SnekTrack; }
import { InkEgg, inkEggIcon } from "./ink-egg.js";
/* Snek: a galaxy snake, drawn and animated entirely in code (Canvas 2D).
 * The body is a tube along a spine of evenly spaced points, so it can coil,
 * slither, rear up and wave its rattle; the head is a vector drawing with its
 * own expressions. Like every pet it plugs into the page as a renderer with
 * setStage() and pose(); it hatches from the shared ink egg. */

const SNEK = {
  body: '#5646e6', shade: '#3b2dbb', deep: '#271c86', light: '#9a90ff',
  throat: '#8a58d6', tongue: '#c83a2b', blush: '#ff8fc8',
  iris: ['#fff4b8', '#ffcc4d', '#dd8418'], pupil: '#1d1530', tear: '#8fd3ff', star: '#ffd54f',
  speckles: ['#ffd54f', '#ffd54f', '#ffd54f', '#ff9ad5', '#ff9ad5', '#b8a8ff', '#ffffff'],
  rings: ['#ff6b6b', '#ffa94d', '#ffe066', '#69db7c', '#4dd4e6'],
  samples: 120, outline: 4.4
};
// Per growth stage: body length and width, head half-width, eye dot and
// sparkle sizes (in head widths), rattle rings, markings, and the resting pose.
const SNEK_STAGES = {
  1: { length: 240, width: 36, head: 50, eye: .078, star: .21, rings: 1, speckles: 9, sparkles: 1, height: 70, tail: [30, 46, 12], bend: 12, sweep: 6 },
  2: { length: 380, width: 44, head: 54, eye: .07, star: .23, rings: 3, speckles: 20, sparkles: 2, height: 120, tail: [42, 76, 16], bend: 22, sweep: 10 },
  3: { length: 520, width: 50, head: 58, eye: .065, star: .25, rings: 5, speckles: 30, sparkles: 4, height: 160, tail: [52, 102, 20], bend: 30, sweep: 14 }
};
const SNEK_WALK_SECONDS = 4.6, SNEK_DEPART_SECONDS = 2.2;

// ---------------------------------------------------------------- geometry
const snekDist = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);
const snekLength = points => { let l = 0; for (let i = 1; i < points.length; i++) l += snekDist(points[i - 1], points[i]); return l; };
function snekBezier(p0, p1, p2, p3, steps) {
  const out = [];
  for (let i = 0; i <= steps; i++) {
    const t = i / steps, u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
    out.push([a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0], a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1]]);
  }
  return out;
}
// Catmull-Rom through points[1 .. n-2]; the first and last points only guide the ends.
function snekSpline(points) {
  const out = [];
  for (let i = 1; i < points.length - 2; i++) {
    const [p0, p1, p2, p3] = [points[i - 1], points[i], points[i + 1], points[i + 2]], steps = Math.max(6, Math.ceil(snekDist(p1, p2) / 5));
    for (let k = i === 1 ? 0 : 1; k <= steps; k++) {
      const t = k / steps, t2 = t * t, t3 = t2 * t, q = j => .5 * (2 * p1[j] + (p2[j] - p0[j]) * t + (2 * p0[j] - 5 * p1[j] + 4 * p2[j] - p3[j]) * t2 + (3 * p1[j] - p0[j] - 3 * p2[j] + p3[j]) * t3);
      out.push([q(0), q(1)]);
    }
  }
  return out;
}
// A polyline of [x, y, ground] points that can be sampled by distance. Beyond
// either end it continues in a straight line.
class SnekTrack {
  cum: any;
  length: any;
  p: any;

  constructor(points) {
    this.p = points; this.cum = [0];
    for (let i = 1; i < points.length; i++) this.cum.push(this.cum[i - 1] + snekDist(points[i - 1], points[i]));
    this.length = this.cum[this.cum.length - 1];
  }
  at(d) {
    const p = this.p, c = this.cum, n = p.length;
    if (d <= 0 || d >= this.length) {
      const [a, b] = d <= 0 ? [p[1], p[0]] : [p[n - 2], p[n - 1]], l = snekDist(a, b) || 1, e = d <= 0 ? -d : d - this.length;
      return [b[0] + (b[0] - a[0]) / l * e, b[1] + (b[1] - a[1]) / l * e, b[2]];
    }
    let lo = 0, hi = n - 1;
    while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (c[mid] <= d) lo = mid; else hi = mid; }
    const k = (d - c[lo]) / ((c[hi] - c[lo]) || 1), a = p[lo], b = p[hi];
    return [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k];
  }
}
function snekSpine(n) {
  const f = () => new Float32Array(n);
  return { n, x: f(), y: f(), g: f(), w: f(), hA: f(), hB: f(), tx: f(), ty: f(), nx: f(), ny: f(), uA: f(), uB: f(), uH: f(), sorted: false };
}
// Samples a track from the head (index 0) to the tail tip.
function snekSample(track, dHead, dTail, n) {
  const sp = snekSpine(n);
  for (let i = 0; i < n; i++) { const q = track.at(dHead + (dTail - dHead) * i / (n - 1)); sp.x[i] = q[0]; sp.y[i] = q[1]; sp.g[i] = q[2]; }
  return sp;
}

// ------------------------------------------------------------------ poses
// Standing: the neck rises from the ground in a soft S into the head, the body
// arcs across the ground towards the viewer, and the tail curls up at the end
// with the rattle. Points run head → tail. The stretch along the ground keeps
// its length, so rearing up or ducking stretches the body a little, as cartoon
// bodies do, and the tail never wanders off.
function snekStanding(st, p) {
  const r = st.width / 2, gy = RIG_GROUND - r;
  const G1 = [240 + p.groundX, gy], H = [G1[0] + p.headDX, gy - p.height];
  const neck = snekBezier(H, [H[0] + p.bend, H[1] + p.height * .45], [G1[0] - p.height * .7, gy - p.height * .04], G1, 40);
  const tail = snekBezier([0, 0], [p.tailX * .9, 0], [p.tailX + p.curl, -p.tailY * .62], [p.tailX, -p.tailY], 30);
  const points = neck.map(q => [q[0], q[1], RIG_GROUND]);
  for (let i = 1; i <= 24; i++) {
    const u = i / 24, arc = Math.sin(Math.PI * u), track = gy + p.sweep * arc;
    points.push([G1[0] + p.ground * u, track - p.hump * arc, track + r]);
  }
  const groundEnd = points.length - 1, x2 = G1[0] + p.ground;
  for (let i = 1; i < tail.length; i++) points.push([x2 + tail[i][0], gy + tail[i][1], RIG_GROUND]);
  return { points, groundEnd };
}
// Asleep: curled in a ring on the ground, tighter as it winds in, with the
// head resting on top and the rattle tucked up at the side.
function snekCoil(st, p) {
  const W = st.width, r = W / 2, base = RIG_GROUND - r, cx = 240 + p.x, neck = W * 1.1, tailOut = W * 1.5;
  const budget = st.length - neck - tailOut, R0 = Math.max(W * 1.05, Math.min(W * 1.75, budget / (Math.PI * 2 * .75))) * p.breath;
  const rise = W * .5, spiral = [];
  let theta = p.start, length = 0, prev = null;
  while (length < budget && spiral.length < 3000) {
    const turns = (theta - p.start) / (Math.PI * 2), R = R0 * (1 - .35 * turns), ground = base + Math.sin(theta) * R * .34;
    const q = [cx + Math.cos(theta) * R, ground - rise * turns, ground + r];
    if (prev) length += snekDist(prev, q);
    spiral.push(prev = q);
    theta += 2 / R;
  }
  // The tail leaves the ring at the front and curls up beside it.
  const S = spiral[0], T = [S[0] + W * 1.05, S[1] - W * 1.15];
  const tail = snekBezier(T, [T[0] + W * .1, T[1] + W * .7], [S[0] + W * .9, S[1]], S, 14).map(q => [q[0], q[1], Math.max(q[1], S[1]) + r]);
  const end = spiral[spiral.length - 1], before = spiral[Math.max(0, spiral.length - 5)], top = (theta - p.start) / (Math.PI * 2);
  const tl = snekDist(before, end) || 1, H = [cx + p.headX, base - rise * top - r * .9 - p.headUp];
  const rising = snekBezier(end, [end[0] + (end[0] - before[0]) / tl * neck * .45, end[1] + (end[1] - before[1]) / tl * neck * .45], [H[0], H[1] + neck * .45], H, 12);
  const points = rising.reverse().map(q => [q[0], q[1], end[2]]);
  for (let i = spiral.length - 2; i >= 0; i--) points.push(spiral[i]);
  for (let i = tail.length - 2; i >= 0; i--) points.push(tail[i]);
  return points;
}
// Slithering follows the leader: the body occupies the stretch of track behind
// the head. It starts from a crouch, so the whole track stays low; the walk
// loops round the meadow and climbs back up the standing neck, and the
// departure heads off to the right.
function snekTrack(st, layout, kind) {
  const r = st.width / 2, gy = RIG_GROUND - r, base = snekStanding(st, layout), b = base.points;
  const crouch = snekStanding(st, { ...layout, height: st.width * 1.3, headDX: -st.width * .2, bend: 0 }).points;
  const reach = Math.max(80, st.length * .3), H = crouch[0], G2 = b[base.groundEnd];
  const start = crouch.slice().reverse();
  // Nearer the viewer is lower on screen: the far side of the loop runs higher.
  const near = gy + 30, far = gy - 48;
  const way = kind === 'walk'
    ? [crouch[4], H, [240 - reach * .9, gy - 18], [240 - reach * 1.15, gy + 4], [240 - reach * .75, near], [240 + reach * .4, near],
      [240 + reach * 1.1, gy + 2], [240 + reach * .85, far], [G2[0] + reach * .25, gy - 30], G2, b[base.groundEnd - 4]]
    : [crouch[4], H, [240 - reach * .9, gy - 14], [240 - reach * 1.15, gy + 6], [240 - reach * .7, near],
      [240 + 260, near], [240 + 1300, near], [240 + 1400, near]];
  // Along the ground the track snakes gently from side to side.
  const loop = snekSpline(way);
  let d = 0;
  const wiggle = loop.map((q, i) => {
    if (i) d += snekDist(loop[i - 1], q);
    const onGround = rigSmooth(gy - 24, gy - 8, q[1]) * rigSmooth(0, 60, d), y = q[1] + Math.sin(d / (st.length * .32) * Math.PI * 2) * 6 * onGround;
    return [q[0], y, Math.max(y, gy) + r];
  });
  const finish = kind === 'walk' ? b.slice(0, base.groundEnd + 1).reverse() : [];
  const points = start.concat(wiggle.slice(1), finish.slice(1));
  return { track: new SnekTrack(points), from: snekLength(start), body: snekLength(start), to: snekLength(points) };
}
function snekLayout(st) {
  const p = { groundX: 0, headDX: st.height * .33, height: st.height, bend: st.bend, tailX: st.tail[0], tailY: st.tail[1], curl: st.tail[2], hump: 0, sweep: st.sweep, ground: 0 };
  // The stretch along the ground takes whatever length the neck and tail leave.
  const probe = snekStanding(st, p).points;
  p.ground = Math.max(20, st.length - snekLength(probe));
  const { points } = snekStanding(st, p);
  let lo = Infinity, hi = -Infinity;
  for (const q of points) { lo = Math.min(lo, q[0] - st.width / 2); hi = Math.max(hi, q[0] + st.width / 2); }
  lo = Math.min(lo, points[0][0] - st.head * 1.05); hi = Math.max(hi, points[0][0] + st.head * 1.05);
  p.groundX = 240 - (lo + hi) / 2;
  return p;
}
// Galaxy markings, fixed for each stage: dots and a few twinkling stars.
function snekMarks(stage, st) {
  let seed = 1103 * stage + 7;
  const rand = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
  const dots = [], stars = [];
  for (let i = 0; i < st.speckles; i++) dots.push({ s: .07 + (i + rand()) / st.speckles * .74, u: (rand() * 2 - 1) * .72, r: (1.7 + rand() * 2.2) * st.width / 50, colour: SNEK.speckles[Math.floor(rand() * SNEK.speckles.length)] });
  for (let i = 0; i < st.sparkles; i++) stars.push({ s: .12 + (i + .5) / st.sparkles * .62 + (rand() - .5) * .06, u: (rand() * 2 - 1) * .45, size: (4.5 + rand() * 2.5) * st.width / 50, phase: rand() * 6 });
  return { dots, stars };
}

// --------------------------------------------------------------- painting
const SNEK_LIGHT = [-.6, -.8], SNEK_LXY = .8, SNEK_LZ = .6, SNEK_TERMINATOR = .45;
// Parts further away (ground line higher on screen) are drawn a little smaller.
const snekDepthScale = g => 1 + (g - RIG_GROUND) * .0025;
// Widths, normals and cel-shading bands for a tube lit from the upper left.
function snekPrepare(sp, st, breathe) {
  const n = sp.n;
  for (let i = 0; i < n; i++) {
    const a = Math.max(0, i - 1), b = Math.min(n - 1, i + 1);
    let tx = sp.x[b] - sp.x[a], ty = sp.y[b] - sp.y[a]; const l = Math.hypot(tx, ty) || 1; tx /= l; ty /= l;
    sp.tx[i] = tx; sp.ty[i] = ty; sp.nx[i] = -ty; sp.ny[i] = tx;
    const s = i / (n - 1);
    sp.w[i] = st.width * (.94 + .06 * rigSmooth(0, .12, s)) * (1 - .48 * rigSmooth(.5, 1, s)) * (1 + (breathe - 1) * (1 - rigSmooth(.5, .8, s))) * snekDepthScale(sp.g[i]);
    const facing = sp.nx[i] * SNEK_LIGHT[0] + sp.ny[i] * SNEK_LIGHT[1], R = Math.hypot(SNEK_LXY * facing, SNEK_LZ);
    const phi = Math.atan2(SNEK_LXY * facing, SNEK_LZ), alpha = Math.acos(Math.min(1, SNEK_TERMINATOR / R));
    sp.uA[i] = Math.sin(Math.min(Math.PI / 2, phi + alpha)); sp.uB[i] = Math.sin(Math.max(-Math.PI / 2, phi - alpha)); sp.uH[i] = Math.sin(phi) * .55;
  }
  // Smooth the shading along the body, so quick twists don't flicker into stripes.
  for (const u of [sp.uA, sp.uB, sp.uH]) {
    const raw = Float32Array.from(u);
    for (let i = 0; i < n; i++) { let sum = 0, count = 0; for (let j = Math.max(0, i - 5); j <= Math.min(n - 1, i + 5); j++) { sum += raw[j]; count++; } u[i] = sum / count; }
  }
  // On the inside of a tight bend the side may not reach past the centre of
  // the bend, or the outline would fold over itself.
  for (let i = 0; i < n; i++) {
    const a = Math.max(0, i - 2), b = Math.min(n - 1, i + 2);
    const turn = Math.atan2(sp.tx[a] * sp.ty[b] - sp.ty[a] * sp.tx[b], sp.tx[a] * sp.tx[b] + sp.ty[a] * sp.ty[b]);
    const bend = turn / (Math.hypot(sp.x[b] - sp.x[a], sp.y[b] - sp.y[a]) || 1), limit = .9 / Math.max(1e-6, Math.abs(bend)), half = sp.w[i] / 2;
    sp.hA[i] = bend > 0 ? Math.min(half, limit) : half; sp.hB[i] = bend < 0 ? Math.min(half, limit) : half;
  }
  for (const h of [sp.hA, sp.hB]) {
    const raw = Float32Array.from(h);
    for (let i = 0; i < n; i++) h[i] = Math.min(raw[i], (raw[Math.max(0, i - 1)] + raw[i] + raw[Math.min(n - 1, i + 1)]) / 3);
  }
}
// Traces a polyline with midpoint curves, so the outline stays smooth.
function snekCurve(path, pts, move) {
  if (move) path.moveTo(pts[0][0], pts[0][1]); else path.lineTo(pts[0][0], pts[0][1]);
  for (let i = 1; i < pts.length - 1; i++) path.quadraticCurveTo(pts[i][0], pts[i][1], (pts[i][0] + pts[i + 1][0]) / 2, (pts[i][1] + pts[i + 1][1]) / 2);
  if (pts.length > 1) path.lineTo(pts[pts.length - 1][0], pts[pts.length - 1][1]);
}
// The body as a union of round segments: a disc at every sample and a band
// between neighbours. Tubes like this stay clean however tightly they bend.
function snekTube(sp, a, b, grow) {
  const path = new Path2D();
  for (let i = a; i <= b; i++) {
    const r = Math.max(.5, sp.w[i] / 2 + grow);
    path.moveTo(sp.x[i] + r, sp.y[i]); path.arc(sp.x[i], sp.y[i], r, 0, Math.PI * 2);
    if (i === b) continue;
    const j = i + 1, a0 = Math.max(.5, sp.hA[i] + grow), a1 = Math.max(.5, sp.hA[j] + grow), b0 = Math.max(.5, sp.hB[i] + grow), b1 = Math.max(.5, sp.hB[j] + grow);
    path.moveTo(sp.x[i] - sp.nx[i] * b0, sp.y[i] - sp.ny[i] * b0); path.lineTo(sp.x[j] - sp.nx[j] * b1, sp.y[j] - sp.ny[j] * b1);
    path.lineTo(sp.x[j] + sp.nx[j] * a1, sp.y[j] + sp.ny[j] * a1); path.lineTo(sp.x[i] + sp.nx[i] * a0, sp.y[i] + sp.ny[i] * a0); path.closePath();
  }
  return path;
}
function snekStarPath(path, x, y, s, turn = 0) {
  const c = Math.cos(turn), d = Math.sin(turn), P = (u: number, v: number): [number, number] => [x + u * c - v * d, y + u * d + v * c];
  path.moveTo(...P(0, -s));
  for (const [u, v, e, f] of [[.12, -.12, 1, 0], [.12, .12, 0, 1], [-.12, .12, -1, 0], [-.12, -.12, 0, -1]]) path.quadraticCurveTo(...P(u * s, v * s), ...P(e * s, f * s));
  path.closePath();
}
// One stretch of body: an ink tube, the body tube just inside it, then shading
// and markings clipped to the body. Neighbouring stretches overlap; the range
// says how far each one's ink and body reach, so no outline crosses the body.
function snekChunk(ctx, sp, st, marks, range, time) {
  const n = sp.n, { inkA, inkB, bodyA, bodyB } = range;
  const at = (i, u) => { const h = u > 0 ? sp.hA[i] : sp.hB[i]; return [sp.x[i] + sp.nx[i] * h * u, sp.y[i] + sp.ny[i] * h * u]; };
  const run = (from, to, f) => { const out = [], step = from <= to ? 1 : -1; for (let i = from; i !== to + step; i += step) out.push(f(i)); return out; };
  const body = snekTube(sp, bodyA, bodyB, -SNEK.outline / 2);
  ctx.fillStyle = RIG_INK; ctx.fill(snekTube(sp, inkA, inkB, SNEK.outline / 2));
  ctx.fillStyle = SNEK.body; ctx.fill(body);
  ctx.save(); ctx.clip(body);
  ctx.fillStyle = SNEK.shade;
  for (const [inner, outer] of ([[i => sp.uA[i], 1.6], [i => sp.uB[i], -1.6]] as [(i: number) => number, number][])) {
    const band = new Path2D();
    snekCurve(band, run(bodyA, bodyB, i => at(i, inner(i))), true); snekCurve(band, run(bodyB, bodyA, i => at(i, outer)), false); band.closePath();
    ctx.fill(band);
  }
  // A soft sheen along the lit side.
  const sheen = new Path2D(); snekCurve(sheen, run(bodyA, bodyB, i => at(i, sp.uH[i])), true);
  ctx.globalAlpha = .32; ctx.strokeStyle = SNEK.light; ctx.lineWidth = st.width * .13; ctx.lineCap = 'round'; ctx.stroke(sheen); ctx.globalAlpha = 1;
  const place = (s, u) => {
    const f = s * (n - 1), i = Math.min(n - 2, Math.floor(f)), k = f - i, h = u > 0 ? sp.hA : sp.hB, half = h[i] + (h[i + 1] - h[i]) * k;
    const x = sp.x[i] + (sp.x[i + 1] - sp.x[i]) * k, y = sp.y[i] + (sp.y[i + 1] - sp.y[i]) * k;
    return [x + sp.nx[i] * half * u, y + sp.ny[i] * half * u, Math.atan2(sp.ty[i], sp.tx[i])];
  };
  for (const dot of marks.dots) {
    const f = dot.s * (n - 1); if (f < bodyA - .5 || f > bodyB + .5) continue;
    const [x, y, angle] = place(dot.s, dot.u);
    ctx.fillStyle = dot.colour; ctx.beginPath(); ctx.ellipse(x, y, dot.r, dot.r * Math.sqrt(1 - dot.u * dot.u), angle, 0, Math.PI * 2); ctx.fill();
  }
  for (const star of marks.stars) {
    const f = star.s * (n - 1); if (f < bodyA - .5 || f > bodyB + .5) continue;
    const [x, y] = place(star.s, star.u), shape = new Path2D();
    snekStarPath(shape, x, y, star.size * (.75 + .25 * Math.abs(Math.sin(time * 2.6 + star.phase))));
    ctx.fillStyle = SNEK.star; ctx.fill(shape); ctx.lineWidth = 1.2; ctx.strokeStyle = RIG_INK; ctx.stroke(shape);
  }
  ctx.restore();
  return body;
}
// The rainbow rattle: beads stacked near the tail tip, which shake when excited.
function snekRattle(ctx, sp, st, shake, time) {
  const n = sp.n, W = st.width, rx = W * .22, gap = rx * 1.6, spacing = Math.hypot(sp.x[n - 1] - sp.x[n - 2], sp.y[n - 1] - sp.y[n - 2]) || 1;
  for (let k = st.rings - 1; k >= 0; k--) {
    const f = Math.max(0, (n - 1) - (rx * 1.35 + k * gap) / spacing), i = Math.min(n - 2, Math.floor(f)), u = f - i;
    const x = sp.x[i] + (sp.x[i + 1] - sp.x[i]) * u, y = sp.y[i] + (sp.y[i + 1] - sp.y[i]) * u, w = sp.w[i] + (sp.w[i + 1] - sp.w[i]) * u;
    const jitter = shake * Math.sin(time * 44 + k * 1.9) * W * .07, ry = Math.max(W * .4 * (1 - k * .025), w / 2 + 4);
    ctx.save(); ctx.translate(x + sp.nx[i] * jitter, y + sp.ny[i] * jitter); ctx.rotate(Math.atan2(sp.ty[i], sp.tx[i]));
    const bead = new Path2D(); bead.ellipse(0, 0, rx, ry, 0, 0, Math.PI * 2);
    ctx.fillStyle = SNEK.rings[k]; ctx.fill(bead);
    ctx.save(); ctx.clip(bead);
    ctx.fillStyle = 'rgba(60,20,80,.22)'; ctx.beginPath(); ctx.ellipse(-rx * .6, ry * .3, rx, ry, 0, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = 'rgba(255,255,255,.65)'; ctx.beginPath(); ctx.ellipse(rx * .1, -ry * .52, rx * .38, ry * .2, 0, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
    ctx.lineWidth = 3.2; ctx.strokeStyle = RIG_INK; ctx.stroke(bead);
    ctx.restore();
  }
}
// A soft shadow on the ground under every part of the body that is near it.
function snekShadow(ctx, sp) {
  const top = [], bottom = [];
  for (let i = 0; i < sp.n; i += 2) {
    const h = sp.g[i] - (sp.y[i] + sp.w[i] / 2), k = sp.w[i] * .24 * Math.max(0, 1 - h / 110);
    top.push([sp.x[i], sp.g[i] - k * .4]); bottom.push([sp.x[i], sp.g[i] + k]);
  }
  const shadow = new Path2D(); snekCurve(shadow, top, true); snekCurve(shadow, bottom.reverse(), false); shadow.closePath();
  ctx.fillStyle = 'rgba(70,90,50,.16)'; ctx.fill(shadow);
}

// Head and lips traced from the reference's idle and win expressions. Each
// contour has matching cubic segments so a small opening starts at the chin.
// Coordinates are in head half-widths; the neck meets the head at (0, .72).
const SNEK_NECK_JOIN = .72;
const SNEK_HEAD_CLOSED = [
  [-.1, -.86], [.13, -1.12, .45, -1.2, .7, -.96], [.96, -.73, 1.05, -.4, .96, -.12],
  [.91, .09, .84, .24, .75, .4], [.65, .56, .56, .7, .43, .83], [.28, 1, .07, 1.09, -.16, 1.09],
  [-.44, 1.1, -.69, 1.05, -.76, .9], [-.84, .7, -.8, .47, -.84, .23],
  [-.85, .03, -.92, -.17, -.97, -.38], [-1.07, -.77, -.88, -.99, -.66, -1.06],
  [-.46, -1.12, -.23, -1.01, -.1, -.86]
];
const SNEK_HEAD_OPEN = [
  [-.25, -.9], [-.02, -1.14, .3, -1.24, .56, -1.08], [.88, -.92, 1.1, -.58, 1.1, -.28],
  [1.08, -.05, 1.23, -.04, 1.29, .1], [1.33, .25, .9, .49, .73, .82], [.55, 1.12, .18, 1.18, -.08, 1.14],
  [-.37, 1.15, -.63, 1.1, -.72, .85], [-.79, .56, -1.09, .39, -1.26, .18],
  [-1.43, -.04, -1.18, .03, -1.05, .02], [-.79, .05, -1.02, -.55, -.85, -.82],
  [-.67, -1.16, -.47, -1.13, -.25, -.9]
];
// The upper lip has an S curve at either corner and a high central arch.
// The lower lip has a narrow chin, rather than a second, mirrored arch.
const SNEK_UPPER_LIP = [
  [-1.24, .08], [-.96, .29, -.79, .21, -.57, -.08],
  [-.27, -.51, -.08, -.52, .17, -.32], [.52, .03, .81, .27, 1.2, .13]
];
const SNEK_LOWER_LIP = [
  [1.2, .13], [.97, .31, .78, .53, .58, .94], [.42, 1.1, .13, .99, -.05, 1.01],
  [-.28, 1.03, -.48, 1.12, -.61, .87], [-.68, .5, -.96, .38, -1.24, .08]
];
const snekHeadPaths = new Map(), snekMouthPaths = new Map();
const snekLerp = (a, b, k) => a + (b - a) * k;
function snekContour(points: number[][], close = true) {
  const path = new Path2D(); path.moveTo(points[0][0], points[0][1]);
  for (const curve of points.slice(1)) path.bezierCurveTo(curve[0], curve[1], curve[2], curve[3], curve[4], curve[5]);
  if (close) path.closePath();
  return path;
}
function snekHeadPath(open) {
  const key = Math.round(open * 100);
  if (!snekHeadPaths.has(key)) {
    const k = key / 100;
    snekHeadPaths.set(key, snekContour(SNEK_HEAD_CLOSED.map((seg, i) => seg.map((v, j) => snekLerp(v, SNEK_HEAD_OPEN[i][j], k)))));
  }
  return snekHeadPaths.get(key);
}
function snekMouth(o) {
  const key = Math.round(o * 100);
  if (!snekMouthPaths.has(key)) {
    const k = key / 100;
    // Closed lips meet at the bottom of the face, never across its middle.
    const blend = points => points.map(seg => seg.map((v, i) => i % 2 ? snekLerp(1.02, v, k) : snekLerp(v * .42 - .08, v, k)));
    const top = blend(SNEK_UPPER_LIP), bottom = blend(SNEK_LOWER_LIP);
    const path = snekContour([...top, ...bottom.slice(1)]);
    const sample = points => {
      const pts = []; let prev = points[0];
      for (const curve of points.slice(1)) {
        pts.push(...snekBezier(prev, curve.slice(0, 2), curve.slice(2, 4), curve.slice(4), 24));
        prev = curve.slice(4);
      }
      return x => {
        for (let i = 1; i < pts.length; i++) {
          const [x0, y0] = pts[i - 1], [x1, y1] = pts[i];
          if ((x - x0) * (x - x1) <= 0) return snekLerp(y0, y1, (x - x0) / ((x1 - x0) || 1));
        }
        return pts[x < pts[0][0] ? 0 : pts.length - 1][1];
      };
    };
    snekMouthPaths.set(key, { path, upperPath: snekContour(top, false), upper: sample(top), lower: sample(bottom) });
  }
  return snekMouthPaths.get(key);
}
// Spray-paint clusters of tiny dots, like the galaxy speckles in the drawings.
const SNEK_HEAD_DOTS = (() => {
  let seed = 4242; const rand = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
  const colours = ['#d9e14a', '#d9e14a', '#ffd54f', '#ff52d4'], dots = [];
  for (const [cx, cy, spread, count] of [[.55, -.62, .17, 9], [-.72, .5, .15, 8], [.8, .25, .1, 5], [-.48, -.82, .08, 4], [.12, .78, .1, 4]])
    for (let i = 0; i < count; i++) { const a = rand() * Math.PI * 2, d = Math.sqrt(rand()) * spread; dots.push([cx + Math.cos(a) * d, cy + Math.sin(a) * d, .012 + rand() * .016, colours[Math.floor(rand() * colours.length)]]); }
  return dots;
})();
// The sparkle on the forehead: a slim four-pointed star in gold, taller than wide.
function snekSparkle(ctx, x, y, s, turn) {
  const shape = new Path2D(), c = Math.cos(turn), d = Math.sin(turn), P = (u: number, v: number): [number, number] => [x + u * c - v * d, y + u * d + v * c];
  shape.moveTo(...P(0, -s));
  for (const [u, v, e, f] of [[.09, -.09, .62, 0], [.09, .09, 0, 1], [-.09, .09, -.62, 0], [-.09, -.09, 0, -1]]) shape.quadraticCurveTo(...P(u * s, v * s), ...P(e * s, f * s));
  shape.closePath();
  ctx.fillStyle = '#dcbc2f'; ctx.fill(shape); ctx.lineWidth = .014; ctx.strokeStyle = '#9c7d16'; ctx.stroke(shape);
  ctx.save(); ctx.clip(shape); ctx.fillStyle = '#f7e27a'; ctx.beginPath(); ctx.ellipse(x - .05 * s, y - .1 * s, .14 * s, .45 * s, turn, 0, Math.PI * 2); ctx.fill(); ctx.restore();
}
function snekEye(ctx, face, st, side) {
  const r = st.eye, ink = RIG_INK, gx = face.gaze.x * .05, gy = face.gaze.y * .04;
  const line = (width, draw) => { ctx.strokeStyle = ink; ctx.lineWidth = width; ctx.lineCap = 'round'; ctx.beginPath(); draw(); ctx.stroke(); };
  if (face.eyes === 'happy') { line(.044, () => { ctx.moveTo(-.12, .06); ctx.bezierCurveTo(-.15, -.2, .07, -.25, .12, .015); }); return; }
  if (face.eyes === 'sleep') { line(.036, () => { ctx.moveTo(-.13, -.03); ctx.quadraticCurveTo(0, .14, .13, -.03); }); return; }
  if (face.eyes === 'shocked') {
    ctx.fillStyle = '#fffdf8'; ctx.beginPath(); ctx.ellipse(0, 0, r * 2.1, r * 2.4, 0, 0, Math.PI * 2); ctx.fill(); ctx.lineWidth = .026; ctx.strokeStyle = ink; ctx.stroke();
    ctx.fillStyle = ink; ctx.beginPath(); ctx.ellipse(gx, gy, r * .85, r * 1.05, 0, 0, Math.PI * 2); ctx.fill();
    return;
  }
  // Open: a small black dot that squashes shut to blink.
  if (face.lids > .85) line(.03, () => { ctx.moveTo(-r * 1.3, gy); ctx.lineTo(r * 1.3, gy); });
  else { ctx.fillStyle = ink; ctx.beginPath(); ctx.ellipse(gx, gy, r, r * 1.35 * (1 - face.lids * .9), 0, 0, Math.PI * 2); ctx.fill(); }
  if (face.eyes === 'sad') line(.03, () => { ctx.moveTo(side * .17, -.18); ctx.lineTo(-side * .1, -.27); });
}
// The forked tongue: thin and red with an ink edge. Hanging from the chin, or
// curling out of the open mouth over the lower jaw.
function snekTongue(ctx, points, ext, wag) {
  if (ext < .03) return;
  const [p0, p1, p2] = points, tip = [p0[0] + (p2[0] - p0[0]) * ext + wag * .04, p0[1] + (p2[1] - p0[1]) * ext], c = [p0[0] + (p1[0] - p0[0]) * ext, p0[1] + (p1[1] - p0[1]) * ext];
  const dir = Math.atan2(tip[1] - c[1], tip[0] - c[0]), path = new Path2D();
  path.moveTo(p0[0], p0[1]); path.quadraticCurveTo(c[0], c[1], tip[0], tip[1]);
  for (const k of [-1, 1]) { path.moveTo(tip[0], tip[1]); const a = dir + k * .55 + wag * .35; path.lineTo(tip[0] + Math.cos(a) * .12 * ext, tip[1] + Math.sin(a) * .12 * ext); }
  ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  ctx.strokeStyle = RIG_INK; ctx.lineWidth = .11; ctx.stroke(path);
  ctx.strokeStyle = SNEK.tongue; ctx.lineWidth = .072; ctx.stroke(path);
}
function snekHead(ctx, h, st, time, neck) {
  const a = h.a, face = h.face, ink = RIG_INK, o = face.open;
  const f = h.turn * snekLerp(.26, .08, o), eyeY = snekLerp(.1, -.53, o);
  // The head casts a dark band across the top of the neck.
  if (neck) {
    ctx.save(); ctx.clip(neck); ctx.globalAlpha = .55; ctx.fillStyle = SNEK.deep;
    ctx.beginPath(); ctx.ellipse(h.x - Math.sin(h.angle) * .42 * a, h.y + Math.cos(h.angle) * .42 * a, .55 * a, .2 * a, h.angle, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
  }
  ctx.save(); ctx.translate(h.x, h.y); ctx.rotate(h.angle); ctx.scale(a, a); ctx.translate(0, -SNEK_NECK_JOIN);
  const shape = snekHeadPath(o);
  ctx.fillStyle = SNEK.body; ctx.fill(shape);
  ctx.save(); ctx.clip(shape);
  // A narrow rim of shade on the lower right, so the head sits with the body's shading.
  ctx.globalAlpha = .7; ctx.fillStyle = SNEK.shade; ctx.fill(shape); ctx.globalAlpha = 1;
  ctx.save(); ctx.translate(-.07, -.08); ctx.fillStyle = SNEK.body; ctx.fill(shape); ctx.restore();
  for (const [x, y, r, c] of SNEK_HEAD_DOTS) { ctx.fillStyle = c; ctx.beginPath(); ctx.arc(x + f * .5 * (1 - Math.abs(x)), y, r, 0, Math.PI * 2); ctx.fill(); }
  // The pale shine around the right eye.
  ctx.fillStyle = '#7e77f6'; ctx.beginPath(); ctx.ellipse(.43 + f, eyeY - .01, .15, .19, -.35, 0, Math.PI * 2); ctx.fill();
  ctx.restore();
  for (const side of [-1, 1]) {
    const far = h.turn * side < 0 ? 1 - Math.abs(h.turn) * .3 : 1 + Math.abs(h.turn) * .04;
    ctx.save(); ctx.translate(side * .42 + f, eyeY + (face.eyes === 'sad' ? .03 : 0)); ctx.scale(far, 1); snekEye(ctx, face, st, side); ctx.restore();
  }
  snekSparkle(ctx, -.25 + .2 * o + f, -.6 - .1 * o, st.star * (1 + .06 * Math.sin(time * 3)), -.12 + Math.sin(time * .7) * .05);
  if (face.eyes === 'sad') {
    // A big blue sweat drop.
    const x = .36 + f, y = -.66 + Math.min(.08, (time * .1) % .5), s = .085;
    ctx.beginPath(); ctx.moveTo(x, y - s * 2.2); ctx.quadraticCurveTo(x + s * 1.1, y - s * .3, x + s, y + s * .3); ctx.arc(x, y + s * .3, s, 0, Math.PI); ctx.quadraticCurveTo(x - s * 1.1, y - s * .3, x, y - s * 2.2);
    ctx.fillStyle = '#7fd3f7'; ctx.fill(); ctx.lineWidth = .016; ctx.strokeStyle = '#3d8fc0'; ctx.stroke();
  }
  ctx.strokeStyle = ink; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  let tongue = [[-.3 + f, 1.04], [-.55 + f, 1.52], [-.4 + f, 2.02]];
  if (o > .04) {
    // The jaw drops open at the bottom of the head.
    const m = snekMouth(o);
    ctx.save(); ctx.clip(shape);
    ctx.fillStyle = '#dfabf2'; ctx.fill(m.path);
    ctx.save(); ctx.clip(m.path);
    // Shade follows the lips, with a flat plane at the back of the mouth.
    ctx.strokeStyle = '#b682c8'; ctx.lineWidth = .16 * o; ctx.stroke(m.path);
    const shade = new Path2D('M.95,.08C.5,.25 .25,.68 .1,1.1L.75,1.18L1.3,.05Z');
    ctx.save(); ctx.translate(0, 1.02 * (1 - o)); ctx.scale(.42 + .58 * o, o); ctx.fillStyle = '#b582c1'; ctx.fill(shade); ctx.restore();
    ctx.restore();
    const fang = (x, up, colour, size) => {
      const len = .4 * size * o, y = up ? m.lower(x) + .015 : m.upper(x) - .015, dir = up ? -1 : 1, w = .1 * size * (.4 + .6 * o);
      ctx.beginPath(); ctx.moveTo(x - w, y); ctx.bezierCurveTo(x - w * .8, y + dir * len * .5, x - w * .3, y + dir * len, x + .06, y + dir * len); ctx.quadraticCurveTo(x + w, y + dir * len * .65, x + w, y); ctx.closePath();
      ctx.fillStyle = colour; ctx.fill(); ctx.lineWidth = .03; ctx.strokeStyle = ink; ctx.stroke();
    };
    if (o > .15) {
      const toothX = x => snekLerp(x * .42 - .08, x, o);
      fang(toothX(-.62), false, '#fffdf8', 1); fang(toothX(.2), false, '#b4b13c', .9);
      fang(toothX(-.46), true, '#fffdf8', .8); fang(toothX(.44), true, '#fffdf8', 1.2);
    }
    ctx.lineWidth = .055; ctx.strokeStyle = ink; ctx.stroke(m.path);
    ctx.restore();
    // The tongue rises from the back of the mouth, arcs over and hangs out over the lower jaw.
    tongue = o > .3 ? [[.46, snekLerp(1.02, .57, o)], [-.38, snekLerp(1.02, -.16, o)], [-.4, 1.25 + .38 * o]] : null;
  }
  ctx.lineWidth = Math.min(SNEK.outline / a, .06); ctx.strokeStyle = ink; ctx.stroke(shape);
  if (tongue) snekTongue(ctx, tongue, o > .3 ? .75 + .25 * o : face.tongue, face.wag);
  ctx.restore();
}

// Paints a whole scene: shadow, body (in depth order), head, then effects.
function snekPaint(ctx, scene) {
  const { sp, st, marks, time } = scene, n = sp.n;
  let length = 0; for (let i = 1; i < n; i++) length += Math.hypot(sp.x[i] - sp.x[i - 1], sp.y[i] - sp.y[i - 1]);
  // Stretches overlap by enough that each one's ink caps fall under a body.
  const spacing = length / (n - 1) || 1, m = Math.ceil((st.width / 2 + SNEK.outline) / spacing / 2) + 1, size = Math.max(10, 2 * m + 4);
  snekShadow(ctx, sp);
  const chunks = [];
  for (let c0 = 0, k = 0; c0 < n - 1; c0 += size, k++) {
    const c1 = n - 1 - (c0 + size) < m ? n - 1 : c0 + size;
    let depth = 0; for (let i = c0; i <= c1; i++) depth += sp.g[i];
    chunks.push({ k, c0, c1, order: -k, depth: depth / (c1 - c0 + 1) });
    if (c1 === n - 1) break;
  }
  const items = chunks.slice();
  items.push({ head: true, order: 1, depth: sp.g[0] + .5 });
  // The rattle goes on top of the stretch of tail it is threaded on.
  const base = Math.max(0, Math.floor((n - 1) - st.width * .22 * (1.35 + 1.6 * (st.rings - 1) + 1) / spacing) - m), under = chunks.find(c => base <= c.c1) || chunks[chunks.length - 1];
  items.push({ rattle: true, order: under.order + .5, depth: under.depth + .01 });
  items.sort((p, q) => sp.sorted ? (p.depth - q.depth) || (p.order - q.order) : p.order - q.order);
  items.forEach((item, i) => { item.pos = i; });
  const clamp = i => Math.max(0, Math.min(n - 1, i));
  let neck = null;
  for (const item of items) {
    if (item.head) { snekHead(ctx, scene.head, st, time, neck); continue; }
    if (item.rattle) { snekRattle(ctx, sp, st, scene.shake, time); continue; }
    const prev = chunks[item.k - 1], next = chunks[item.k + 1];
    const range = {
      inkA: clamp(prev && prev.pos < item.pos ? item.c0 + m : item.c0 - m), inkB: clamp(next && next.pos < item.pos ? item.c1 - m : item.c1 + m),
      bodyA: clamp(item.c0 - m), bodyB: clamp(item.c1 + m)
    };
    const body = snekChunk(ctx, sp, st, marks, range, time);
    if (item.c0 === 0) neck = body;
  }
  for (const fx of scene.effects) rigEffects[fx.type](ctx, fx, time);
}

// ---------------------------------------------------------------- the rig
class SnekRig {
  resizeObserver?: ResizeObserver;
  blendAt: any;
  blinkAt: any;
  canvas: any;
  ctx: any;
  doubleBlink: any;
  face: any;
  flickAt: any;
  from: any;
  info: any;
  lastTime: any;
  layout: any;
  marks: any;
  motion: any;
  nextBlink: any;
  nextFlick: any;
  nextRattle: any;
  rattleAt: any;
  rigSvg: any;
  root: any;
  shown: any;
  small: any;
  st: any;
  stage: any;
  tracks: any;

  constructor(mount, options: { small?: boolean } = {}) {
    this.small = !!options.small; this.stage = null; this.motion = null; this.shown = null; this.from = null;
    this.nextBlink = 1.5 + Math.random() * 2; this.blinkAt = -10; this.doubleBlink = false;
    this.nextFlick = 1 + Math.random() * 2; this.flickAt = -10; this.nextRattle = 6 + Math.random() * 4; this.rattleAt = -10;
    this.face = { open: 0 };
    this.root = document.createElement('div'); this.root.className = 'illustrated-pet';
    this.root.innerHTML = '<canvas class="pet-canvas"></canvas><svg class="illustrated-rig" viewBox="0 0 480 480" aria-hidden="true"></svg>';
    mount.append(this.root);
    this.canvas = this.root.querySelector('canvas'); this.ctx = this.canvas.getContext('2d'); this.rigSvg = this.root.querySelector('svg');
    this.resize();
    if (typeof ResizeObserver === 'function') { this.resizeObserver = new ResizeObserver(() => this.resize()); this.resizeObserver.observe(this.root); }
  }
  dispose(): void { this.resizeObserver?.disconnect(); this.root.remove(); }
  resize() {
    const size = Math.round(Math.min(1400, Math.max(64, (this.root.clientWidth || 340) * Math.min(3, devicePixelRatio || 1))));
    if (this.canvas.width !== size || this.canvas.height !== size) this.canvas.width = this.canvas.height = size;
  }
  setStage(stage) {
    if (this.stage === stage) return;
    this.stage = stage; this.st = SNEK_STAGES[stage]; this.layout = snekLayout(this.st); this.marks = snekMarks(stage, this.st);
    this.tracks = {}; this.shown = this.from = null; this.motion = null;
  }
  track(kind) { return this.tracks[kind] || (this.tracks[kind] = snekTrack(this.st, this.layout, kind)); }
  // What each move does to the body, the face and the effects around it.
  choreograph(frame) {
    const { motion: m, elapsed: e, time: t } = frame, live = frame.animate ? 1 : 0, st = this.st, B = this.layout;
    const p = { ...B }, face: SnekFace = { eyes: 'open', mouth: 'smile', open: 0, tongue: 'flick', blink: true, gaze: { x: 0, y: 0 } };
    const plan: SnekPlan = { kind: 'stand', p, face, lift: 0, tilt: 0, turn: 0, breathe: 1 + live * Math.sin(t * 2) * .02, shake: 0, effects: new Set() };
    p.height += live * Math.sin(t * 2) * 3; p.headDX += live * Math.sin(t * .9) * 5; p.bend += live * Math.sin(t * 1.1) * 7;
    p.tailX += live * Math.sin(t * 1.5 + 1) * 5; p.tailY += live * Math.sin(t * 1.9) * 4; p.curl += live * Math.sin(t * 1.7) * 8;
    plan.tilt = live * Math.sin(t * .8) * .04;
    switch (m) {
      case 'drag':
        p.height = B.height * .95; plan.tilt = live * Math.sin(e * 24) * .025;
        Object.assign(face, { eyes: 'shocked', open: 1, tongue: 'none', blink: false }); break;
      case 'slide': {
        const vx = frame.velocity?.x ?? 450, lean = live * Math.max(-1, Math.min(1, vx / 900));
        p.height = B.height * .8; p.headDX += lean * 20; p.bend -= lean * 18;
        p.tailY = B.tailY * .6; p.curl -= lean * 24; plan.tilt = lean * .1;
        Object.assign(face, { eyes: e < .35 ? 'shocked' : 'open', tongue: 'none' }); break;
      }
      case 'wobble': {
        const envelope = live * Math.max(0, 1 - e / 1.2) ** 2, sway = Math.sin(e * 17) * envelope;
        p.headDX += sway * 34; p.bend += Math.sin(e * 17 - .7) * envelope * 28;
        p.tailX -= sway * 20; p.curl += Math.sin(e * 17 - 1.6) * envelope * 38;
        p.height -= Math.abs(sway) * B.height * .12; plan.tilt = -sway * .15; plan.shake = envelope * .7;
        Object.assign(face, { eyes: e < .35 ? 'shocked' : 'open', tongue: 'none' }); break;
      }
      case 'flap': case 'toss':
        plan.kind = 'air'; plan.tilt = live * Math.sin(e * 12) * .08; plan.shake = live * .8;
        Object.assign(face, { eyes: 'shocked', open: 1, tongue: 'none', blink: false }); break;
      case 'land': {
        plan.kind = 'land'; plan.coilAmount = live * (1 - rigSmooth(.12, .95, e));
        const recoil = live * Math.exp(-e * 5) * Math.sin(e * 22);
        plan.coil = { x: -8, start: Math.PI * .32, headX: recoil * 12, headUp: 8, breath: 1 + recoil * .09 };
        plan.tilt = recoil * .14; plan.shake = live * Math.max(0, 1 - e * 2);
        Object.assign(face, { eyes: e < .25 ? 'happy' : 'open', tongue: 'none' }); break;
      }
      case 'happy': {
        const cheer = e >= 1.55, bounce = Math.abs(Math.sin(e * 6.5));
        p.height = B.height * (cheer ? 1.12 : 1) + bounce * (cheer ? 10 : 16); p.bend = B.bend + Math.sin(e * 6.5) * 14;
        p.tailY = B.tailY * (cheer ? 1.45 : 1.2); p.curl = B.curl + Math.sin(e * (cheer ? 16 : 13)) * (cheer ? 14 : 18);
        plan.tilt = Math.sin(e * 4.4) * .08; plan.shake = cheer ? 1 : .3;
        Object.assign(face, { eyes: 'happy', open: cheer ? 1 : .95, tongue: 'none' }); plan.effects.add('sparkles'); break;
      }
      case 'wave':
        p.tailY = B.tailY * 1.6; p.tailX = B.tailX * 1.2; p.curl = B.curl + Math.sin(e * 9.5) * 38; p.height += Math.abs(Math.sin(e * 3.2)) * 3;
        plan.tilt = .1 + Math.sin(e * 3.2) * .03; plan.shake = .6;
        Object.assign(face, { eyes: 'happy', open: .9, tongue: 'none', gaze: { x: .6, y: -.2 } }); plan.effects.add('swish'); break;
      case 'hop': {
        const c = (e % 1.05) / 1.05;
        if (c < .18) { const q = Math.sin(c / .18 * Math.PI); p.height = B.height * (1 - .22 * q); plan.breathe = 1 + .06 * q; }
        else if (c < .8) { const q = Math.sin((c - .18) / .62 * Math.PI); plan.lift = q * 46; p.height = B.height * (1 + .14 * q); p.tailY = B.tailY * (1 - .5 * q); p.bend = B.bend * (1 - .5 * q); }
        else { const q = Math.sin((c - .8) / .2 * Math.PI); p.height = B.height * (1 - .18 * q); plan.breathe = 1 + .05 * q; }
        Object.assign(face, { eyes: 'happy', open: .85, tongue: 'none' }); break;
      }
      case 'walk': case 'depart':
        plan.kind = m; Object.assign(face, { tongue: 'often' }); break;
      case 'peck': {
        const q = Math.pow(Math.max(0, Math.sin(e * 5.2)), 1.4);
        p.height = B.height * (1 - .62 * q); p.headDX = B.headDX - 14 * q; p.bend = B.bend + 10 * q; plan.tilt = .1 * q;
        Object.assign(face, { open: q > .7 ? (q - .7) / .3 * .7 : 0, tongue: 'none', gaze: { x: 0, y: .9 } }); break;
      }
      case 'sleep':
        plan.kind = 'coil'; plan.coil = { x: -8, start: Math.PI * .32, headX: 6, headUp: 0, breath: 1 + live * Math.sin(t * 1.25) * .015 };
        plan.tilt = -.16 + live * Math.sin(t * 1.25) * .02;
        Object.assign(face, { eyes: 'sleep', tongue: 'none', blink: false }); plan.effects.add('snooze'); break;
      case 'sad':
        p.height = B.height * .72 + Math.sin(e * 1.6) * 2; p.bend = B.bend + 16; p.tailY = B.tailY * .35; p.curl = B.curl + 20;
        plan.tilt = -.16 + live * Math.sin(t * 1.1) * .02;
        Object.assign(face, { eyes: 'sad', tongue: 'hang', gaze: { x: 0, y: .5 } }); break;
      case 'surprised':
        p.height = B.height * 1.22 + Math.max(0, 1 - e * 3) * 20; p.bend = B.bend * .3; p.headDX += Math.sin(e * 42) * Math.max(0, .45 - e) * 8;
        p.tailY = B.tailY * 1.3; plan.shake = Math.max(0, 1 - e);
        Object.assign(face, { eyes: 'shocked', open: .5, tongue: 'none', blink: false });
        if (e < 1.7) plan.effects.add('shock'); break;
      case 'hatch': {
        const env = Math.max(0, 1 - e / 2);
        p.bend = B.bend + Math.sin(e * 11) * 26 * env; p.headDX += Math.sin(e * 11 + 1) * 10 * env; p.hump = Math.abs(Math.sin(e * 5.5)) * 10 * env;
        p.curl += Math.sin(e * 13) * 20 * env; plan.tilt = Math.sin(e * 11) * .08 * env;
        Object.assign(face, { eyes: 'happy', open: .65, tongue: 'none' }); break;
      }
      default: {
        // Idle: a curious head tilt now and then, and a little rattle every so often.
        const k = t % 7.3, curious = rigSmooth(3.4, 3.9, k) * (1 - rigSmooth(5, 5.6, k));
        plan.tilt += live * curious * .14 * (Math.floor(t / 7.3) % 2 ? 1 : -1);
        if (live && t >= this.nextRattle) { this.rattleAt = t; this.nextRattle = t + 7 + Math.random() * 6; }
        const r = t - this.rattleAt; plan.shake = r < .7 ? Math.sin(r / .7 * Math.PI) : 0;
      }
    }
    return plan;
  }
  // Builds the spine for a plan: standing, coiled or following a track.
  build(plan, frame) {
    const st = this.st, n = SNEK.samples;
    let sp;
    if (plan.kind === 'air') {
      // Unfurl into an S in the direction of travel; the rattle lags behind the head.
      const velocity = frame.velocity ?? { x: 480, y: frame.motion === 'flap' ? -600 : 300 };
      const direction = velocity.x < 0 ? -1 : 1, rising = velocity.y < -80;
      const reach = Math.min(260, st.length * .6), points = [];
      for (let i = 0; i <= 60; i++) {
        const s = i / 60, wave = frame.animate ? Math.sin(s * Math.PI * 3 - frame.elapsed * 15) : 0;
        points.push([240 + direction * reach * (.45 - s),
          260 + s * (rising ? 120 : 65) + wave * Math.sin(s * Math.PI) * st.width * .65, RIG_GROUND]);
      }
      const track = new SnekTrack(points); sp = snekSample(track, 0, track.length, n);
    } else if (plan.kind === 'walk' || plan.kind === 'depart') {
      const { track, from, body, to } = this.track(plan.kind), dur = plan.kind === 'walk' ? SNEK_WALK_SECONDS : SNEK_DEPART_SECONDS;
      const k = Math.min(1, frame.elapsed / dur), progress = plan.kind === 'walk' ? k * k * (3 - 2 * k) : k * k;
      const head = from + (to - from) * progress;
      sp = snekSample(track, head, head - body, n); sp.sorted = true;
      // The head rides above the ground while slithering.
      const ramp = rigSmooth(0, .5, frame.elapsed) * (plan.kind === 'walk' ? 1 - rigSmooth(dur - .6, dur, frame.elapsed) : 1), raise = st.height * .4 * ramp;
      for (let i = 0; i < n; i++) { const s = i / (n - 1); if (s < .22) sp.y[i] -= raise * (1 - s / .22) ** 2; }
      plan.track = track;
    } else {
      const points = plan.kind === 'coil' ? snekCoil(st, plan.coil) : snekStanding(st, plan.p).points;
      const track = new SnekTrack(points);
      sp = snekSample(track, 0, track.length, n);
      if (plan.kind === 'land' && plan.coilAmount) {
        const coilTrack = new SnekTrack(snekCoil(st, plan.coil));
        const coil = snekSample(coilTrack, 0, coilTrack.length, n), k = plan.coilAmount;
        for (let i = 0; i < n; i++) { sp.x[i] += (coil.x[i] - sp.x[i]) * k; sp.y[i] += (coil.y[i] - sp.y[i]) * k; sp.g[i] += (coil.g[i] - sp.g[i]) * k; }
      }
    }
    if (plan.lift) for (let i = 0; i < n; i++) sp.y[i] -= plan.lift;
    return sp;
  }
  pose(frame: PetFrame) {
    if (this.stage == null) return null;
    if (document.hidden) return this.info;
    this.resize();
    const time = frame.time, st = this.st, dt = this.lastTime == null ? 0 : Math.max(0, Math.min(.1, time - this.lastTime));
    this.lastTime = time;
    if (frame.motion !== this.motion) {
      if (this.shown && frame.animate) { this.from = this.shown; this.blendAt = time; }
      this.motion = frame.motion;
    }
    const plan = this.choreograph(frame);
    let sp = this.build(plan, frame);
    // Changing moves eases from the last drawn body into the new one.
    const blendSeconds = ['flap', 'toss', 'land'].includes(frame.motion) ? .14 : .45;
    if (this.from && time - this.blendAt < blendSeconds) {
      const k = rigSmooth(0, blendSeconds, time - this.blendAt), from = this.from;
      for (let i = 0; i < sp.n; i++) { sp.x[i] = from.x[i] + (sp.x[i] - from.x[i]) * k; sp.y[i] = from.y[i] + (sp.y[i] - from.y[i]) * k; sp.g[i] = from.g[i] + (sp.g[i] - from.g[i]) * k; }
    } else this.from = null;
    snekPrepare(sp, st, plan.breathe);
    // The face: eases the mouth, blinks, flicks the tongue and follows the pointer.
    const face = plan.face;
    this.face.open = frame.animate ? this.face.open + (face.open - this.face.open) * Math.min(1, dt * 16) : face.open;
    face.open = this.face.open;
    face.lids = face.eyes === 'open' && face.blink ? rigBlink(this, time, frame.animate) : 0;
    let tongue = 0, wag = 0;
    if (face.tongue === 'hang') { tongue = .8; wag = Math.sin(time * 2) * .3; }
    else if ((face.tongue === 'flick' || face.tongue === 'often') && frame.animate && face.open < .05) {
      if (time >= this.nextFlick && time - this.flickAt > 1.4) { this.flickAt = time; this.nextFlick = time + (face.tongue === 'often' ? 1.6 + Math.random() : 2.4 + Math.random() * 2.4); }
      const k = time - this.flickAt; tongue = k < .12 ? k / .12 : k < 1.1 ? 1 : k < 1.25 ? 1 - (k - 1.1) / .15 : 0; wag = k < 1.1 ? Math.sin(k * 26) * (k < .5 ? 1 : .35) : 0;
    }
    face.tongue = tongue; face.wag = wag;
    face.gaze = { x: Math.max(-1, Math.min(1, face.gaze.x + frame.gaze.x)), y: Math.max(-1, Math.min(1, face.gaze.y + frame.gaze.y)) };
    // The head stays mostly upright and turns towards where it is going.
    const dx = sp.x[0] - sp.x[4], dy = sp.y[0] - sp.y[4], dl = Math.hypot(dx, dy) || 1;
    const lean = Math.max(-.6, Math.min(.6, Math.atan2(dx, -dy)));
    let turn = -.7 + frame.gaze.x * .3;
    if (plan.kind === 'walk' || plan.kind === 'depart') turn = Math.max(-1, Math.min(1, dx / dl * .9));
    const head = { x: sp.x[0], y: sp.y[0], a: st.head * snekDepthScale(sp.g[0]), angle: lean * .35 + plan.tilt, turn, face };
    // Effects sit around the head and the rattle.
    const c = Math.cos(head.angle), s = Math.sin(head.angle), a = st.head, hx = head.x + s * SNEK_NECK_JOIN * a, hy = head.y - c * SNEK_NECK_JOIN * a;
    const effects = [];
    if (plan.effects.has('sparkles')) effects.push({ type: 'sparkle', x: hx - 1.42 * a, y: hy - .62 * a, size: a * .3, phase: 0 }, { type: 'sparkle', x: hx + 1.45 * a, y: hy - .38 * a, size: a * .24, phase: 1.7 }, { type: 'sparkle', x: hx + 1.12 * a, y: hy - 1.25 * a, size: a * .17, phase: 3.1 });
    if (plan.effects.has('shock')) effects.push({ type: 'shock', x: hx - 1.2 * a, y: hy - 1.02 * a, size: a * .42, angle: -2.4, age: frame.elapsed, phase: 0 }, { type: 'shock', x: hx + 1.2 * a, y: hy - 1.02 * a, size: a * .42, angle: -.74, age: frame.elapsed, phase: 2 });
    if (plan.effects.has('snooze')) effects.push({ type: 'snooze', x: hx + .85 * a, y: hy - 1.2 * a, size: a * .3 });
    if (plan.effects.has('swish')) { const i = sp.n - 1; effects.push({ type: 'swish', x: sp.x[i], y: sp.y[i], angle: -.35, size: st.width * .95, speed: 9.5, phase: 0 }); }
    const ctx = this.ctx, k = this.canvas.width / RIG_STAGE;
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.setTransform(k, 0, 0, k, 0, 0);
    snekPaint(ctx, { sp, st, marks: this.marks, head, shake: plan.shake, effects, time });
    this.canvas.style.opacity = frame.showRig && !this.small ? '.55' : '';
    this.shown = sp;
    this.drawRig(sp, plan, frame.showRig);
    this.root.dataset.currentMotion = frame.motion; this.root.dataset.pose = plan.kind; this.root.dataset.eyes = face.eyes; this.root.dataset.eyelids = face.lids.toFixed(2);
    this.info = { rootX: 0, lift: plan.lift, ownShadow: true, pose: plan.kind, head: [head.x, head.y], tail: [sp.x[sp.n - 1], sp.y[sp.n - 1]], turn: head.turn, eyelids: face.lids, tongue: face.tongue };
    return this.info;
  }
  // Show rig: the spine, its joints and, while slithering, the track ahead.
  drawRig(sp, plan, show) {
    this.rigSvg.classList.toggle('visible', show);
    if (!show) return;
    const pts = []; for (let i = 0; i < sp.n; i++) pts.push(sp.x[i].toFixed(1) + ',' + sp.y[i].toFixed(1));
    let svg = '<g class="mesh-lines"><path d="M' + pts.join('L') + '" style="stroke-width:1.6;opacity:.9"/>';
    if (plan.track) svg += '<path d="M' + plan.track.p.filter((_, i) => i % 3 === 0).map(q => q[0].toFixed(1) + ',' + q[1].toFixed(1)).join('L') + '" style="stroke-dasharray:4 5"/>';
    svg += '</g><g class="rig-marks">';
    const labels = { 0: 'head', [Math.round(sp.n * .3)]: 'body', [Math.round(sp.n * .7)]: 'tail', [sp.n - 1]: 'rattle' };
    for (let i = 0; i < sp.n; i += 8) svg += '<circle cx="' + sp.x[i].toFixed(1) + '" cy="' + sp.y[i].toFixed(1) + '" r="3"/>';
    for (const [i, label] of Object.entries(labels)) svg += '<circle cx="' + sp.x[i].toFixed(1) + '" cy="' + sp.y[i].toFixed(1) + '" r="4.5"/><text x="' + (sp.x[i] + 8).toFixed(1) + '" y="' + (sp.y[i] - 7).toFixed(1) + '">' + label + '</text>';
    this.rigSvg.innerHTML = svg + '</g>';
  }
}

// The egg Snek hatches from: long and leathery, with violet freckles and a few gold stars.
const SNEK_EGG = (() => {
  const freckles = [[-34, -150, 5], [22, -168, 4], [44, -122, 6], [-46, -98, 4.5], [-8, -126, 3.5], [30, -78, 5.5], [-30, -50, 4.5], [50, -56, 3.5], [6, -34, 4], [-52, -140, 3], [-16, -182, 3], [58, -98, 3], [-56, -70, 3.5], [22, -18, 3]];
  const stars = [[-26, -112, 9, .2], [36, -150, 7, -.15], [12, -56, 6, .1]];
  const star = (x, y, s, r) => { const p = []; for (const [u, v] of [[0, -1], [1, 0], [0, 1], [-1, 0]]) p.push([u * s, v * s]); return '<path d="M' + p[0].join(',') + 'Q' + (.12 * s) + ',' + (-.12 * s) + ' ' + p[1].join(',') + 'Q' + (.12 * s) + ',' + (.12 * s) + ' ' + p[2].join(',') + 'Q' + (-.12 * s) + ',' + (.12 * s) + ' ' + p[3].join(',') + 'Q' + (-.12 * s) + ',' + (-.12 * s) + ' ' + p[0].join(',') + 'Z" transform="translate(' + x + ' ' + y + ') rotate(' + (r * 57) + ')" fill="#ffd54f" stroke="' + RIG_INK + '" stroke-width="2"/>'; };
  return {
    shape: 'M0,-196C42,-196 70,-148 70,-96C70,-40 42,1 0,1C-42,1 -70,-40 -70,-96C-70,-148 -42,-196 0,-196Z',
    weight: 'M68,-62C60,-22 36,1 0,1C-28,1-50,-12-62,-34',
    crack: [[-70, -108], [-56, -98], [-44, -114], [-30, -98], [-16, -114], [-2, -98], [12, -114], [26, -98], [40, -114], [54, -100], [70, -110]],
    smallCrack: 'M18,-184L25,-172L17,-163L27,-152', sideCrack: 'M-69,-108L-56,-98L-44,-114L-32,-101',
    shell: ['#fdfbff', '#efe8ff', '#cdbcf2'],
    markings: '<ellipse cx="4" cy="-8" rx="64" ry="22" fill="#a996e0" opacity=".3"/>' +
      freckles.map(([x, y, r]) => '<ellipse cx="' + x + '" cy="' + y + '" rx="' + r + '" ry="' + (r * .8) + '" fill="#7b68d8" opacity=".55"/>').join('') +
      stars.map(q => star(q[0], q[1], q[2], q[3])).join('') +
      '<path d="M-40,-150C-50,-128-53,-108-52,-88" stroke="#ffffff" stroke-width="9" stroke-linecap="round" fill="none" opacity=".9"/><circle cx="-31" cy="-168" r="4.5" fill="#fff"/>'
  };
})();

function createSnekRenderer(mount: HTMLElement, options: { small?: boolean }) {
  return new StagedRenderer(mount, [
    { stages: [0], create: host => new InkEgg(host, SNEK_EGG) },
    { stages: [1, 2, 3], create: host => new SnekRig(host, options) }
  ]);
}
// Stage icons: the egg, then Snek at each stage in its resting pose.
function snekStageIcon(stage) {
  if (stage === 0) return inkEggIcon(SNEK_EGG);
  const canvas = document.createElement('canvas'), st = SNEK_STAGES[stage], layout = snekLayout(st);
  canvas.width = canvas.height = 96; canvas.setAttribute('aria-hidden', 'true');
  const track = new SnekTrack(snekStanding(st, layout).points), sp = snekSample(track, 0, track.length, SNEK.samples);
  snekPrepare(sp, st, 1);
  let x0 = Infinity, x1 = -Infinity, y0 = Infinity, y1 = -Infinity;
  for (let i = 0; i < sp.n; i++) { x0 = Math.min(x0, sp.x[i] - sp.w[i]); x1 = Math.max(x1, sp.x[i] + sp.w[i]); y1 = Math.max(y1, sp.y[i] + sp.w[i] / 2); }
  x0 = Math.min(x0, sp.x[0] - st.head * 1.1); x1 = Math.max(x1, sp.x[0] + st.head * 1.1); y0 = sp.y[0] - st.head * 1.9;
  const k = 90 / Math.max(x1 - x0, y1 - y0), ctx = canvas.getContext('2d');
  ctx.setTransform(k, 0, 0, k, 48 - (x0 + x1) / 2 * k, 93 - y1 * k);
  const face = { eyes: 'open', mouth: 'smile', open: 0, lids: 0, tongue: 0, wag: 0, gaze: { x: 0, y: 0 } };
  snekPaint(ctx, { sp, st, marks: snekMarks(stage, st), head: { x: sp.x[0], y: sp.y[0], a: st.head, angle: 0, turn: 0, face }, shake: 0, effects: [], time: 0 });
  return canvas;
}

export { createSnekRenderer as createRenderer, snekStageIcon as stageIcon };
