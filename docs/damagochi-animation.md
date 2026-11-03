# How Damagochi pet animation works

Damagochi uses custom TypeScript with the browser's WebGL and Canvas 2D APIs.
The animation loop is driven by `requestAnimationFrame`. There is no external
animation or physics library in this implementation.

Screen movement and character animation are separate: physics chooses where the
pet is, while a motion state chooses its expression and body pose. The chicken
deforms existing illustrated artwork; Snek is drawn along a changing body curve.

The code below is copied from the implementation. The complete movement module
can be reused as a module; the other blocks are excerpts from their surrounding
functions or classes and retain those functions' variables and helpers.

## Source map

| File | Responsibility |
| --- | --- |
| [pet-host.ts](../DeveAgentManager/Browser/pet-host.ts) | Pointer input, motion states, screen position and frame loop. |
| [types.ts](../DeveAgentManager/Browser/pets/types.ts) | Shared frame and renderer interfaces. |
| [throw-motion.ts](../DeveAgentManager/Browser/pets/throw-motion.ts) | Release velocity, icy slides and gravity throws. |
| [silkie-art.ts](../DeveAgentManager/Browser/pets/silkie-art.ts) | Chicken drawings, wing anchors and choreography. |
| [illustrated-rig.ts](../DeveAgentManager/Browser/pets/illustrated-rig.ts) | WebGL mesh deformation, eye overlays and Canvas fallback. |
| [snake-art.ts](../DeveAgentManager/Browser/pets/snake-art.ts) | Snek's body curves, face and Canvas painting. |
| [playground.ts](../DeveAgentManager/Browser/pets/playground.ts) | Interactive preview using the same renderers and movement module. |

## 1. What each frame contains

`time` is the animation clock in seconds. `elapsed` is the time since the current
motion began. `velocity` is screen movement in pixels per second. `gaze` tells
the renderer where to look. Each renderer implements `setStage` and `pose`.

```typescript
export interface PetFrame {
    time: number; motion: string; elapsed: number; gaze: { x: number; y: number };
    animate: boolean; showRig: boolean; progress: number; transitioning: boolean;
    velocity?: { x: number; y: number };
}
export interface RendererOptions { small?: boolean; }
export interface PetRenderer {
    setStage(stage: number): void;
    pose(frame: PetFrame): { rootX: number; lift: number; ownShadow?: boolean } | null;
    dispose?(): void;
}
export interface PetArtModule {
    prepare?(): Promise<void>;
    createRenderer(mount: HTMLElement, options: RendererOptions): PetRenderer;
}
```

## 2. Turning a drag into a drop, slide or throw

Pointer capture keeps the gesture attached to the pet when the pointer leaves
its bounds. Once movement exceeds seven pixels, the host shows the scared
`drag` pose and makes the screen position follow the pointer.

Source: `pet-host.ts`, inside `initialize`.

```typescript
body.addEventListener("pointermove", e => {
    if (!drag || e.pointerId !== drag.id) return;
    const dx = e.clientX - drag.startX, dy = e.clientY - drag.startY;
    if (!drag.moved && Math.hypot(dx, dy) > 7) {
        drag.moved = true; h.dragging = true; h.dragAt = h.time;
        clearTimeout(longPress); root.classList.add("dragging");
    }
    if (drag.moved) { h.x = drag.x + dx; h.y = drag.y + dy; clamp(h); }
    drag.velocity.add(h.x, h.y, e.timeStamp);
    if (drag.moved) start(h);
}, { signal });
```

At release, the host calculates velocity from the final 100 ms, then tries a
throw before trying a slide. These lines run after gesture cleanup and the
drag-to-hide check, when movement is allowed:

```typescript
h.velocity = velocity;
h.flight = beginThrow(h.x, h.y, velocity, h.snapshot?.size ?? 140, throwBounds(h)) ?? undefined;
h.slide = h.flight ? undefined : beginSlide(h.x, h.y, velocity) ?? undefined;
h.motion = h.flight ? "flap" : h.slide ? "slide" : "idle";
h.motionAt = h.time; h.nextIdleAt = h.time + 12;
```

| Release | Result |
| --- | --- |
| Any upward direction, total speed at least 700 px/s | Throw with gravity. |
| Other moving release at least 40 px/s | Slide without gravity. |
| Release below 40 px/s, including dragging and then holding still | Drop directly to normal. |

Screen Y increases downward, so negative vertical velocity means upward.
Downward and exactly horizontal releases always use slide/drop behavior.
Initial movement speed is capped at 1500 px/s.

This is the complete `throw-motion.ts` module, including release sampling,
exponential slide friction, gravity and viewport boundaries:

```typescript
export interface Velocity { x: number; y: number; }
export interface ThrowBounds { left: number; top: number; right: number; bottom: number; }
export interface PetSlide { x: number; y: number; vx: number; vy: number; }
export interface PetFlight extends PetSlide { floor: number; }
export const THROW_SPEED = 700;

// Use only the end of the gesture: dragging and then holding still is a drop.
export class PointerVelocity {
    private samples: { x: number; y: number; at: number }[] = [];
    add(x: number, y: number, at: number): void {
        this.samples.push({ x, y, at });
        // Keep the point just before the window too, so slower pointer events still have a direction.
        while (this.samples.length > 2 && this.samples[1].at <= at - 100) this.samples.shift();
        this.samples = this.samples.slice(-20);
    }
    release(x: number, y: number, at: number): Velocity {
        this.add(x, y, at);
        const first = this.samples[0], next = this.samples[1], start = Math.max(first.at, at - 100);
        const seconds = (at - start) / 1000;
        if (seconds < .008) return { x: 0, y: 0 };
        const fraction = next.at > first.at ? (start - first.at) / (next.at - first.at) : 0;
        const startX = first.x + (next.x - first.x) * fraction, startY = first.y + (next.y - first.y) * fraction;
        return { x: (x - startX) / seconds, y: (y - startY) / seconds };
    }
}

export function beginThrow(x: number, y: number, velocity: Velocity, size: number, bounds: ThrowBounds): PetFlight | null {
    const speed = Math.hypot(velocity.x, velocity.y);
    // Every upward angle can throw, but only a deliberate fast flick lifts off.
    if (velocity.y >= 0 || speed < THROW_SPEED) return null;
    const scale = Math.min(1, 1500 / speed);
    // A little virtual floor below the release keeps the pet near its chosen position.
    return { x, y, vx: velocity.x * scale, vy: velocity.y * scale, floor: Math.min(bounds.bottom, y + size * .75) };
}

export function beginSlide(x: number, y: number, velocity: Velocity): PetSlide | null {
    const speed = Math.hypot(velocity.x, velocity.y);
    if (speed < 40) return null;
    const scale = Math.min(1, 1500 / speed);
    return { x, y, vx: velocity.x * scale, vy: velocity.y * scale };
}

export function advanceSlide(slide: PetSlide, seconds: number, bounds: ThrowBounds): boolean {
    const dt = Math.max(0, Math.min(.05, seconds)), friction = 1.8, drag = Math.exp(-friction * dt);
    // The shove stays on one plane: both axes coast and slow equally, without gravity.
    slide.x += slide.vx * (1 - drag) / friction; slide.y += slide.vy * (1 - drag) / friction;
    slide.vx *= drag; slide.vy *= drag;
    if (slide.x < bounds.left || slide.x > bounds.right) {
        slide.x = Math.max(bounds.left, Math.min(bounds.right, slide.x)); slide.vx = 0;
    }
    if (slide.y < bounds.top || slide.y > bounds.bottom) {
        slide.y = Math.max(bounds.top, Math.min(bounds.bottom, slide.y)); slide.vy = 0;
    }
    return Math.hypot(slide.vx, slide.vy) < 18;
}

export function advanceThrow(flight: PetFlight, seconds: number, bounds: ThrowBounds): boolean {
    const dt = Math.max(0, Math.min(.05, seconds)), drag = Math.exp(-.8 * dt);
    flight.x += flight.vx * (1 - drag) / .8;
    flight.vx *= drag;
    flight.y += flight.vy * dt + .5 * 1600 * dt * dt;
    flight.vy += 1600 * dt;
    if (flight.x < bounds.left || flight.x > bounds.right) {
        flight.x = Math.max(bounds.left, Math.min(bounds.right, flight.x));
        flight.vx *= -.28;
    }
    if (flight.y < bounds.top) { flight.y = bounds.top; flight.vy = Math.max(0, flight.vy); }
    flight.floor = Math.max(bounds.top, Math.min(bounds.bottom, flight.floor));
    if (flight.y >= flight.floor && flight.vy >= 0) { flight.y = flight.floor; return true; }
    return false;
}
```

The slide applies the same friction to both axes, so it coasts in its original
direction and stops below 18 px/s. A throw accelerates downward at 1600 px/s²
while horizontal momentum decays. Its virtual floor is at most three quarters
of the pet's size below the release position, bounded by the viewport.

## 3. Updating position and choosing a pose

The host advances physics once per frame and passes the resulting velocity to
the renderer. A slide returns directly to `idle` when it stops. A throw changes
from `flap` to `toss` as it stops rising, then plays `land` at the virtual floor.

Source: the frame loop in `pet-host.ts`.

```typescript
const dt = h.last ? Math.min(.05, (timestamp - h.last) / 1000) : 0;
h.time += dt; h.last = timestamp;
if (reduced) stopMovement(h);
if (h.slide) {
    const stopped = advanceSlide(h.slide, dt, throwBounds(h));
    h.x = h.slide.x; h.y = h.slide.y; h.velocity = { x: h.slide.vx, y: h.slide.vy }; clamp(h);
    h.motion = "slide";
    if (stopped) { h.slide = undefined; h.velocity = undefined; h.motion = "idle"; h.motionAt = h.time; savePosition(h); }
}
if (h.flight) {
    const landed = advanceThrow(h.flight, dt, throwBounds(h));
    h.x = h.flight.x; h.y = h.flight.y; h.velocity = { x: h.flight.vx, y: h.flight.vy }; clamp(h);
    const motion = landed ? "land" : h.flight.vy < -80 ? "flap" : "toss";
    if (h.motion !== motion) { h.motion = motion; h.motionAt = h.time; }
    if (landed) { h.flight = undefined; savePosition(h); }
}
```

The host then supplies the chosen pose to the renderer and schedules another
frame. Active dragging overrides the ordinary pose; the other expressions here
also account for hiding threats and low happiness.

```typescript
const motion = h.dragging && !h.snapshot!.needsAdoption ? "drag" : h.threatened && !h.snapshot!.needsAdoption ? "sad" : h.motion === "working" ? "idle" : h.motion === "idle" && h.snapshot!.happiness < 25 ? "sad" : h.motion;
h.root.dataset.motion = motion;
h.renderer!.pose({ time: reduced ? 0 : h.time, motion, elapsed: reduced ? .8 : h.time - (h.dragging ? h.dragAt : h.motionAt), gaze: reduced ? { x: 0, y: 0 } : h.gaze,
    animate: !reduced, showRig: false, progress: h.snapshot!.stageProgress, transitioning: h.targetStage !== undefined, velocity: h.velocity });
if (!reduced) h.frame = requestAnimationFrame(draw);
```

Hidden tabs pause drawing. Reduced motion stops slide/throw movement and renders
still expressions. Catching the pet, opening its menu or starting a farewell
also stops ongoing movement.

## 4. Making the chicken flap

Each growth stage has an image atlas containing different expressions and poses.
Flight uses the `hop` drawing, cell 10, which already has both feathered wings
spread. The wings stay on that drawing during ascent and descent.

The choreography uses `e` for elapsed seconds, `m` for the motion and `live` for
whether animation is enabled (`1` or `0`). A sine wave supplies the repeated
wing rotation. The other waves move its feet, head and crest, while horizontal
velocity adds a lean in the direction of travel.

Source: `silkieChoreography` in `silkie-art.ts`.

```typescript
case 'flap': case 'toss': {
  const vx = frame.velocity?.x ?? 480, vy = frame.velocity?.y ?? (m === 'flap' ? -600 : 300);
  cell = SILKIE_CELL.hop;
  // Use the drawing with both feathered wings spread for the entire flight.
  const beat = Math.sin(e * (m === 'flap' ? 44 : 40));
  wings = live * (.02 + beat * .14);
  feet = live * Math.sin(e * 34) * 1.5;
  tilt = live * (Math.max(-.22, Math.min(.22, vx / 3600)) + Math.sin(e * 29) * .035);
  head = -tilt * .6 + live * Math.sin(e * 37) * .045; crest += live * Math.sin(e * 33) * 7;
  squash = 1 + live * (Math.min(.04, Math.abs(vy) / 24000) + Math.sin(e * 26) * .015); blink = false;
  show.add('shock');
  break;
}
```

`44` and `40` are angular speeds in radians per second, roughly seven and six
flap cycles per second. `.14` controls the wing rotation amplitude. This deforms
the spread-wing illustration rather than simulating aerodynamic lift; the
screen trajectory comes from the separate gravity calculation.

Each wing has a measured pivot, center and elliptical region. For the flight
drawing, these fractions of the image cell are:

```typescript
[SILKIE_CELL.hop]: { pivot: [.22, .56], centre: [.10, .42], radius: [.12, .19] },
```

The right wing mirrors the left around the drawing's center. The choreography
converts the measured regions into rotation operations, using opposite signs
for the two wings:

```typescript
const anchor = SILKIE_WINGS[cell] || SILKIE_WINGS[SILKIE_CELL.idle];
for (const side of ['left', 'right']) {
  const raised = cell === SILKIE_CELL.wave && SILKIE_WAVE_SIDE[stage] === side;
  const a = cell === SILKIE_CELL.wave && !raised ? SILKIE_WINGS[SILKIE_CELL.idle] : anchor;
  const u = side === 'left' ? x => x * W : x => 2 * mid - x * W, sign = side === 'left' ? 1 : -1;
  ops.push({ type: 'rotate', pivot: [u(a.pivot[0]), a.pivot[1] * H], angle: sign * (raised ? wave : wings),
    region: { ellipse: [u(a.centre[0]), a.centre[1] * H, a.radius[0] * W, a.radius[1] * H] }, label: 'wing' });
  if (raised && show.has('swish')) swish = [u(a.centre[0] - .05), (a.centre[1] - .16) * H, side === 'left' ? -2.3 : -.84];
}
```

## 5. Applying those operations to the artwork

The illustrated rig lays a 22 × 26 grid over the drawing. Each rectangular cell
becomes two triangles. Rotation, translation and scale operations affect nearby
grid points according to smooth region weights; the influence fades at the
edges to keep the body connected to the animated part.

Source: `illustrated-rig.ts`.

```typescript
function rigWeight(region, x, y) {
  if (!region) return 1;
  if (region.ellipse) {
    const [cx, cy, rx, ry] = region.ellipse;
    return 1 - rigSmooth(.55, 1.15, Math.hypot((x - cx) / rx, (y - cy) / ry));
  }
  if (region.above) return 1 - rigSmooth(region.above[0], region.above[1], y);
  if (region.box) {
    const [x0, y0, x1, y1, soft] = region.box;
    return rigSmooth(x0 - soft, x0 + soft, x) * (1 - rigSmooth(x1 - soft, x1 + soft, x)) * rigSmooth(y0 - soft, y0 + soft, y) * (1 - rigSmooth(y1 - soft, y1 + soft, y));
  }
  return 0;
}
function rigDeform(ops, x, y) {
  let px = x, py = y;
  for (const op of ops) {
    const w = rigWeight(op.region, x, y);
    if (w <= 0) continue;
    if (op.type === 'rotate') {
      const a = op.angle * w, c = Math.cos(a), s = Math.sin(a), dx = px - op.pivot[0], dy = py - op.pivot[1];
      px = op.pivot[0] + dx * c - dy * s; py = op.pivot[1] + dx * s + dy * c;
    } else if (op.type === 'shift') {
      px += op.dx * w; py += op.dy * w;
    } else if (op.type === 'scale') {
      px = op.pivot[0] + (px - op.pivot[0]) * (1 + (op.sx - 1) * w);
      py = op.pivot[1] + (py - op.pivot[1]) * (1 + (op.sy - 1) * w);
    }
  }
  return [px, py];
}
```

After deformation, `toStage` scales the drawing onto a 480 × 480 stage, anchors
its feet and applies the whole-body tilt and lift:

```typescript
toStage(pose, x, y) {
  const [dx, dy] = rigDeform(pose.ops, x, y), g = pose.global;
  const px = (dx - g.footX) * g.units * g.sx, py = (dy - g.footY) * g.units * g.sy;
  return [g.x + px * g.cos - py * g.sin, g.y + px * g.sin + py * g.cos];
}
```

`drawMesh` fills vertex positions and texture coordinates from that grid:

```typescript
const x0 = Math.max(0, cell.minX - 6), x1 = Math.min(cell.width, cell.maxX + 7);
const y0 = Math.max(0, cell.minY - 6), y1 = Math.min(cell.height, cell.maxY + 7);
let i = 0;
for (let row = 0; row <= this.rows; row++) for (let col = 0; col <= this.cols; col++) {
  const x = x0 + (x1 - x0) * col / this.cols, y = y0 + (y1 - y0) * row / this.rows, [sx, sy] = this.toStage(pose, x, y);
  this.positions[i] = sx; this.positions[i + 1] = sy;
  this.texcoords[i] = (cell.x + x) * scale / texture.width; this.texcoords[i + 1] = (cell.y + y) * scale / texture.height;
  i += 2;
}
```

WebGL draws the textured triangles at the updated positions. A Canvas 2D
fallback draws the selected image with the whole-body transforms when WebGL
is unavailable; it does not perform the local wing mesh deformation.

Panicked flight eyes are painted on a separate Canvas overlay, mapped through
the same deformation. This keeps the spread feathers intact while changing
the expression. The choreography sets `panicEyes` for `flap` and `toss`.

```typescript
drawPanicEye(ctx, k, pose, eye, gaze) {
  const [cx, cy] = this.toStage(pose, eye.x, eye.y);
  const [ax, ay] = this.toStage(pose, eye.x + eye.rx, eye.y), [bx, by] = this.toStage(pose, eye.x, eye.y + eye.ry);
  ctx.save(); ctx.setTransform((ax - cx) * k, (ay - cy) * k, (bx - cx) * k, (by - cy) * k, cx * k, cy * k);
  ctx.beginPath(); ctx.ellipse(0, 0, 1.02, 1.02, 0, 0, Math.PI * 2);
  ctx.fillStyle = '#fffdf4'; ctx.fill(); ctx.strokeStyle = RIG_INK; ctx.lineWidth = .09; ctx.stroke();
  const x = gaze.x * .12, y = gaze.y * .1;
  ctx.beginPath(); ctx.ellipse(x, y, .43, .53, 0, 0, Math.PI * 2); ctx.fillStyle = '#66401e'; ctx.fill();
  ctx.beginPath(); ctx.ellipse(x, y, .29, .4, 0, 0, Math.PI * 2); ctx.fillStyle = RIG_INK; ctx.fill();
  ctx.beginPath(); ctx.ellipse(x - .13, y - .2, .12, .15, 0, 0, Math.PI * 2); ctx.fillStyle = '#fffdf4'; ctx.fill();
  ctx.restore();
}
```

## 6. Scared dragging and continuous slide wobble

Dragging uses the scared drawing with a small tremble. Sliding uses the former
off-balance wobble continuously, with strength tied to current speed rather than
expiring after a fixed animation duration. It fades as the slide slows down.

Source: `silkieChoreography` in `silkie-art.ts`.

```typescript
case 'drag':
  cell = SILKIE_CELL.shocked; blink = false;
  wings = .12; tilt = live * Math.sin(e * 24) * .018; head = -tilt; break;
case 'slide': case 'wobble': {
  const sliding = m === 'slide';
  const envelope = live * (sliding ? Math.min(1, Math.hypot(frame.velocity?.x ?? 450, frame.velocity?.y ?? 0) / 300) : Math.max(0, 1 - e / 1.2) ** 2);
  const sway = Math.sin(e * 18) * envelope;
  cell = sliding || e < .45 ? SILKIE_CELL.shocked : SILKIE_CELL.idle;
  if (sliding) blink = false;
  tilt = sway * .18; head -= sway * .15; crest += sway * 8;
  wings = Math.abs(sway) * .3; feet = sway; squash = 1 - Math.abs(sway) * .04;
  break;
}
```

A drag-and-drop without release momentum goes straight to normal. There is no
off-balance recovery after either a stationary drop or a completed slide. The
chicken rig also suppresses its usual drawing-change squash for this return:

```typescript
if (pose.cell !== this.cell) {
  // Releasing a drag or finishing a glide returns quietly to the idle drawing.
  const resting = frame.motion === 'idle' && (this.motion === 'drag' || this.motion === 'slide');
  if (this.cell >= 0) this.popAt = resting ? -Infinity : time;
  this.cell = pose.cell;
}
this.motion = frame.motion;
```

## 7. Animating Snek

Snek uses a procedural Canvas 2D renderer. Its body is sampled along a curve,
then painted with thickness, markings, a head and a rattle. Changing the curve
bends the whole body without needing a new drawing for every frame.

Its drag, slide, flight and landing choreography sets body parameters and face
expressions. Sliding repeats a wave through the head, body and tail; flight
switches to an airborne curve with panicked eyes and a fully open mouth.

Source: `choreograph` in `snake-art.ts`.

```typescript
case 'drag':
  p.height = B.height * .95; plan.tilt = live * Math.sin(e * 24) * .025;
  Object.assign(face, { eyes: 'shocked', open: 1, tongue: 'none', blink: false }); break;
case 'slide': case 'wobble': {
  const sliding = m === 'slide';
  const envelope = live * (sliding ? Math.min(1, Math.hypot(frame.velocity?.x ?? 450, frame.velocity?.y ?? 0) / 300) : Math.max(0, 1 - e / 1.2) ** 2);
  const sway = Math.sin(e * 17) * envelope;
  p.headDX += sway * 34; p.bend += Math.sin(e * 17 - .7) * envelope * 28;
  p.tailX -= sway * 20; p.curl += Math.sin(e * 17 - 1.6) * envelope * 38;
  p.height -= Math.abs(sway) * B.height * .12; plan.tilt = -sway * .15; plan.shake = envelope * .7;
  Object.assign(face, { eyes: sliding || e < .35 ? 'shocked' : 'open', tongue: 'none' });
  if (sliding) Object.assign(face, { open: 1, blink: false });
  break;
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
```

The airborne curve uses the direction of travel, with a traveling sine wave
along the body. `SnekTrack` and `snekSample` turn these points into the body
samples used by the painter:

```typescript
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
```

Other branches build standing, walking and coiled poses. Before painting,
Snek blends the previous body into the new one to soften state changes:

```typescript
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
```

## 8. Playground and verification

The playground imports these same renderers and `throw-motion.ts`. The offline
builder embeds the browser modules, frame metadata and chicken atlases into one
HTML file so it can run without a server.

From the repository root, install the pinned toolchain if needed, then type-check
and run the interaction checks:

```sh
npm ci --ignore-scripts
npm run check
npm run test:pets
```

`npm run test:pets` compiles the TypeScript, checks release classification and
physics, builds the offline playground, and uses Playwright to exercise the real
browser host and renderers. It requires Playwright Chromium to be installed.
Coverage includes both species, scared dragging, stationary drops, slides,
upward throws, reduced motion, touch input, boundaries and playground poses.

To build a named offline playground after compilation:

```sh
node .typescript/tools/pet-playground/build.mjs .validation/pet-playground.html
```

The builder is [tools/pet-playground/build.mts](../tools/pet-playground/build.mts).
Edit the TypeScript sources in `DeveAgentManager/Browser`; generated JavaScript
under `wwwroot/js` and `.typescript` is produced by the build.

