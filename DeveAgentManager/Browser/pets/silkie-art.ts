import { IllustratedRig, StagedRenderer, loadRigAtlas, RIG_INK, rigSmooth } from "./illustrated-rig.js";
import { InkEgg, inkEggIcon } from "./ink-egg.js";
const ease = (n: number) => rigSmooth(0, 1, n);
let silkieFrames: any;
export async function prepare(): Promise<void> {
 const response = await fetch(new URL("../../pets/silkie/frames.json", import.meta.url));
 if (!response.ok) throw new Error("Could not load Byte’s artwork.");
 silkieFrames = await response.json();
 await Promise.all(Object.values(SILKIE_ART.stages).map(spec => loadRigAtlas(spec.atlas).promise));
}
/* Silkie: artwork, measured anchors and choreography for the illustrated rig,
 * plus the speckled egg it hatches from. Each pet has its own file like this
 * one; the page and its pet session stay the same. */

// Atlas cells, left to right and top to bottom, in every growth stage.
const SILKIE_CELL = { idle: 0, blink: 1, happy: 2, wave: 3, sad: 4, shocked: 5, sleep: 6, peck: 7, walkLeft: 8, walkRight: 9, hop: 10, cheer: 11 };

// Left-wing anchors as fractions of the cell, measured by hand on
// artwork/debug/*-grid.png: shoulder pivot, wing centre and wing radius. The
// right wing mirrors them about the drawing's centre.
const SILKIE_WINGS = {
  [SILKIE_CELL.idle]: { pivot: [.25, .62], centre: [.14, .68], radius: [.10, .14] },
  [SILKIE_CELL.happy]: { pivot: [.26, .63], centre: [.12, .60], radius: [.13, .16] },
  [SILKIE_CELL.wave]: { pivot: [.23, .63], centre: [.11, .46], radius: [.13, .21] },
  [SILKIE_CELL.hop]: { pivot: [.22, .56], centre: [.10, .42], radius: [.12, .19] },
  [SILKIE_CELL.cheer]: { pivot: [.22, .57], centre: [.10, .39], radius: [.12, .21] }
};
// The chick waves its right wing; the young and adult birds their left.
const SILKIE_WAVE_SIDE = { 1: 'right', 2: 'left', 3: 'left' };

function silkieChoreography(frame, frames, stage) {
  const { motion: m, elapsed: e, time: t, gaze } = frame, live = frame.animate ? 1 : 0;
  let cell = SILKIE_CELL.idle, rootX = 0, lift = 0, tilt = 0, squash = 1, blink = true, feet = 0;
  let head = live * Math.sin(t * 1.3) * .012 + gaze.x * .05, wings = live * Math.sin(t * 2.6) * .02, wave = 0;
  let breathe = live * Math.sin(t * 2.1) * .009, crest = live * Math.sin(t * 2.3 + .6) * 1.6;
  const show = new Set();
  let swish = null;
  switch (m) {
    case 'happy':
      cell = e < 1.55 ? SILKIE_CELL.happy : SILKIE_CELL.cheer;
      lift = Math.abs(Math.sin(e * 6.5)) * 10; tilt = Math.sin(e * 4.4) * .03; wings = .1 + Math.sin(e * 14) * .16;
      show.add('sparkles'); break;
    case 'wave':
      cell = SILKIE_CELL.wave; wave = .06 + Math.sin(e * 9.5) * .24; lift = Math.abs(Math.sin(e * 3.2)) * 2;
      head += SILKIE_WAVE_SIDE[stage] === 'left' ? -.03 : .03; show.add('swish'); break;
    case 'hop': {
      cell = SILKIE_CELL.hop;
      const c = (e % 1.05) / 1.05;
      if (c < .18) squash = 1 - Math.sin(c / .18 * Math.PI) * .07;
      else if (c < .8) { const p = Math.sin((c - .18) / .62 * Math.PI); lift = p * 44; squash = 1 + p * .03; wings = p * .22; }
      else squash = 1 - Math.sin((c - .8) / .2 * Math.PI) * .06;
      break;
    }
    case 'walk': case 'depart': {
      // Walk out, turn round, cross back, and finish where the walk began.
      const step = Math.sin(e * 9);
      let facing = 1;
      if (m === 'walk') {
        const direction = Math.floor((t - e) * 7) % 2 ? 1 : -1, phase = e * Math.PI / 2;
        rootX = Math.sin(phase) * 42 * direction; facing = Math.cos(phase) * direction >= 0 ? 1 : -1;
      } else rootX = ease(e / 1.05) * 440;
      cell = facing > 0 ? SILKIE_CELL.walkRight : SILKIE_CELL.walkLeft;
      lift = Math.abs(step) * 4; tilt = step * .03; feet = step; head -= step * .015; wings = Math.abs(step) * .05;
      break;
    }
    case 'peck': {
      cell = SILKIE_CELL.peck;
      const p = Math.max(0, Math.sin(e * 6));
      tilt = p * .05; lift = -p * 2; squash = 1 - p * .02; break;
    }
    case 'sleep':
      cell = SILKIE_CELL.sleep; breathe = live * Math.sin(t * 1.25) * .016; head = 0; wings = 0; crest *= .4; blink = false;
      show.add('snooze'); break;
    case 'sad':
      cell = SILKIE_CELL.sad; tilt = Math.sin(e * 1.6) * .014; squash = .985; head = -.02 + live * Math.sin(t * 1.1) * .01; wings = -.03;
      break;
    case 'surprised':
      cell = SILKIE_CELL.shocked; lift = Math.max(0, 1 - e * 3) * 16; rootX = Math.sin(e * 42) * Math.max(0, .45 - e) * 7;
      if (e < 1.7) show.add('shock');
      break;
    case 'hatch':
      tilt = Math.sin(e * 13) * .05 * Math.max(0, 1 - e / 2); lift = Math.abs(Math.sin(e * 6.5)) * 3; break;
    default: {
      // Idle: a curious head tilt now and then, and an occasional fluff shake.
      const k = t % 7.3, curious = rigSmooth(3.4, 3.9, k) * (1 - rigSmooth(5, 5.6, k));
      head += live * curious * .07 * (Math.floor(t / 7.3) % 2 ? 1 : -1);
      const r = t % 11.7, ruffle = r > 10.9 ? Math.sin((r - 10.9) * 40) * (1 - (r - 10.9) / .8) : 0;
      crest += live * ruffle * 4; squash *= 1 + live * ruffle * .02;
    }
  }

  const f = frames[cell], W = f.width, H = f.height, h = f.maxY - f.minY;
  const mid = (f.minX + f.maxX) / 2, y = k => f.minY + h * k;
  const ops: { type: string; pivot?: number[]; sx?: number; sy?: number; angle?: number; dx?: number; dy?: number; region: { above?: number[]; ellipse?: number[]; box?: number[] }; label?: string }[] = [
    { type: 'scale', pivot: [f.anchorX, f.maxY], sx: 1 - breathe * .7, sy: 1 + breathe, region: { above: [y(.84), y(.94)] }, label: 'breath' },
    { type: 'rotate', pivot: [mid, y(.66)], angle: head, region: { above: [y(.5), y(.72)] }, label: 'head' },
    { type: 'shift', pivot: [mid, y(.08)], dx: crest + gaze.x * 2, dy: gaze.y * 1.5, region: { above: [y(.1), y(.32)] }, label: 'crest' }
  ];
  const anchor = SILKIE_WINGS[cell] || SILKIE_WINGS[SILKIE_CELL.idle];
  for (const side of ['left', 'right']) {
    const raised = cell === SILKIE_CELL.wave && SILKIE_WAVE_SIDE[stage] === side;
    const a = cell === SILKIE_CELL.wave && !raised ? SILKIE_WINGS[SILKIE_CELL.idle] : anchor;
    const u = side === 'left' ? x => x * W : x => 2 * mid - x * W, sign = side === 'left' ? 1 : -1;
    ops.push({ type: 'rotate', pivot: [u(a.pivot[0]), a.pivot[1] * H], angle: sign * (raised ? wave : wings),
      region: { ellipse: [u(a.centre[0]), a.centre[1] * H, a.radius[0] * W, a.radius[1] * H] }, label: 'wing' });
    if (raised && show.has('swish')) swish = [u(a.centre[0] - .05), (a.centre[1] - .16) * H, side === 'left' ? -2.3 : -.84];
  }
  if (feet) for (const [x0, x1, s] of [[f.minX - 10, f.anchorX - 4, 1], [f.anchorX + 4, f.maxX + 10, -1]])
    ops.push({ type: 'shift', dx: 0, dy: -Math.max(0, feet * s) * 7, region: { box: [x0, y(.84), x1, f.maxY + 12, 8] } });

  // Effects sit around the face: between the eyes when the drawing has them.
  const face = f.eyes?.length === 2 ? { x: (f.eyes[0].x + f.eyes[1].x) / 2, y: (f.eyes[0].y + f.eyes[1].y) / 2, size: Math.abs(f.eyes[1].x - f.eyes[0].x) }
    : { x: mid, y: y(.45), size: (f.maxX - f.minX) * .3 };
  const near = (dx, dy) => [face.x + dx * face.size, face.y + dy * face.size];
  const effects = [];
  if (show.has('sparkles')) effects.push(
    { type: 'sparkle', at: near(-1.3, -.9), size: face.size * .3, phase: 0 },
    { type: 'sparkle', at: near(1.35, -.6), size: face.size * .24, phase: 1.7 },
    { type: 'sparkle', at: near(1.05, -1.5), size: face.size * .17, phase: 3.1 });
  if (show.has('shock')) effects.push(
    { type: 'shock', at: near(-1.2, -.95), size: face.size * .42, angle: -2.4, age: e, phase: 0 },
    { type: 'shock', at: near(1.2, -.95), size: face.size * .42, angle: -.74, age: e, phase: 2 });
  if (show.has('snooze')) effects.push({ type: 'snooze', at: near(.75, -1.05), size: face.size * .32 });
  if (swish) effects.push({ type: 'swish', at: [swish[0], swish[1]], angle: swish[2], size: W * .07, speed: 9.5, phase: 0 });
  return { cell, rootX, lift, tilt, squash, ops, effects, blink };
}

const SILKIE_ART = {
  stages: { 1: { atlas: 'silkie-chick', height: 270 }, 2: { atlas: 'silkie-young', height: 322 }, 3: { atlas: 'silkie-adult', height: 360 } },
  get frames() {
    const data = silkieFrames.stages;
    const frames = { 1: data.chick.frames, 2: data.young.frames, 3: data.adult.frames };
    Object.defineProperty(this, 'frames', { value: frames });
    return frames;
  },
  choreograph: silkieChoreography
};

// The egg the chick hatches from: cream, with chocolate speckles like its coat.
const SILKIE_EGG = (() => {
  const blob = 'M-1,-9C5,-10 10,-5 9,1C8,7 2,10-4,8C-10,6-11,-1-7,-6C-5,-8-3,-9-1,-9Z';
  const spots = [[-36, -150, 1.15, .95, 20], [18, -172, .8, .7, -15], [46, -128, 1.25, 1.05, 35], [-52, -98, .95, .85, -10], [-8, -118, .7, .6, 50],
    [32, -78, 1.2, 1, 10], [-34, -46, .95, .8, -30], [56, -50, .7, .6, 25], [4, -34, .65, .55, 0], [-58, -140, .45, .4, 0],
    [-14, -182, .5, .45, 0], [66, -96, .45, .4, 0], [-62, -64, .5, .45, 0], [24, -14, .45, .4, 0]];
  return {
    shape: 'M0,-200C46,-200 81,-122 81,-74C81,-28 46,1 0,1C-46,1 -81,-28 -81,-74C-81,-122 -46,-200 0,-200Z',
    weight: 'M79,-58C70,-20 40,1 0,1C-30,1-55,-12-70,-34',
    crack: [[-81, -112], [-64, -102], [-52, -118], [-36, -102], [-20, -118], [-4, -102], [12, -118], [28, -102], [44, -118], [60, -104], [81, -114]],
    smallCrack: 'M22,-190L30,-176L22,-166L33,-154', sideCrack: 'M-80,-112L-64,-102L-52,-118L-38,-104',
    shell: ['#fffaf0', '#f8ecd2', '#e8d0a3'],
    markings: '<ellipse cx="4" cy="-8" rx="74" ry="24" fill="#dcc293" opacity=".35"/>' +
      spots.map(([x, y, sx, sy, r]) => '<path d="' + blob + '" fill="#2b231e" transform="translate(' + x + ' ' + y + ') rotate(' + r + ') scale(' + sx + ' ' + sy + ')"/>').join('') +
      '<path d="M-46,-150C-56,-128-60,-108-59,-88" stroke="#fffef9" stroke-width="9" stroke-linecap="round" fill="none" opacity=".9"/><circle cx="-37" cy="-170" r="4.5" fill="#fffef9"/>'
  };
})();

function createSilkieRenderer(mount, options) {
  return new StagedRenderer(mount, [
    { stages: [0], create: host => new InkEgg(host, SILKIE_EGG) },
    { stages: [1, 2, 3], create: host => new IllustratedRig(host, SILKIE_ART, options) }
  ]);
}

// Stage icons use the real artwork: the egg drawing, then each stage's idle drawing.
function silkieStageIcon(stage) {
  if (stage === 0) return inkEggIcon(SILKIE_EGG);
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 96; canvas.setAttribute('aria-hidden', 'true');
  const atlas = loadRigAtlas(SILKIE_ART.stages[stage].atlas);
  atlas.promise.then(() => {
    const f = SILKIE_ART.frames[stage][SILKIE_CELL.idle], w = f.maxX - f.minX + 1, h = f.maxY - f.minY + 1, s = 88 / Math.max(w, h);
    canvas.getContext('2d').drawImage(atlas.image, f.x + f.minX, f.y + f.minY, w, h, (96 - w * s) / 2, 96 - 4 - h * s, w * s, h * s);
  });
  return canvas;
}

export { createSilkieRenderer as createRenderer };
