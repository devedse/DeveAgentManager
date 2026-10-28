import type { PetFrame, PetRenderer, RendererOptions } from "./types.js";
const ease = (n: number) => rigSmooth(0, 1, n);
/* Illustrated pet rig.
 * Draws hand-illustrated expression atlases with WebGL and brings them to life
 * with a soft 2D mesh, procedural eyelids and small ink-style effects. A
 * species supplies its artwork, measured anchors and a choreography; nothing in
 * this file knows about chickens, dogs or snakes.
 *
 * Coordinates: a choreography works in the pixels of one atlas cell. The rig
 * places the drawing on a 480 × 480 unit stage with the feet on RIG_GROUND.
 */
const RIG_STAGE = 480, RIG_GROUND = 442, RIG_INK = '#1f1a16';
const rigSmooth = (a, b, x) => { const n = Math.max(0, Math.min(1, (x - a) / (b - a))); return n * n * (3 - 2 * n); };

// Species atlases load on demand and are shared by every rig.
const rigAtlases = new Map();
function loadRigAtlas(id) {
  let atlas = rigAtlases.get(id);
  if (atlas) return atlas;
  atlas = { id, ready: false, error: null, image: null, padded: new Map() };
  atlas.promise = new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => { atlas.image = image; atlas.ready = true; resolve(atlas); };
    image.onerror = () => { atlas.error = 'The character artwork could not be loaded.'; rigAtlases.delete(id); reject(Error(atlas.error)); };
    image.src = new URL('../../pets/silkie/' + id + '.webp', import.meta.url).href;
  });
  atlas.promise.catch(() => {});
  rigAtlases.set(id, atlas);
  return atlas;
}
// WebGL 1 needs power-of-two textures for mipmaps, which keep the ink lines
// clean when the pet is drawn small. Small rigs use a half-size copy.
function paddedRigAtlas(atlas, scale) {
  if (atlas.padded.has(scale)) return atlas.padded.get(scale);
  const pot = n => 2 ** Math.ceil(Math.log2(n));
  const canvas = document.createElement('canvas');
  canvas.width = pot(atlas.image.naturalWidth * scale); canvas.height = pot(atlas.image.naturalHeight * scale);
  canvas.getContext('2d').drawImage(atlas.image, 0, 0, atlas.image.naturalWidth * scale, atlas.image.naturalHeight * scale);
  atlas.padded.set(scale, canvas);
  return canvas;
}

// Region weights for mesh operations, evaluated on undeformed cell pixels.
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

// Blinks at natural, slightly random intervals, sometimes twice in a row.
// Returns how far the eyes are closed (0 to 1). State lives on the owner.
function rigBlink(owner, time, animate) {
  if (!animate) return 0;
  if (time >= owner.nextBlink && time - owner.blinkAt > .5) {
    owner.blinkAt = time; owner.doubleBlink = Math.random() < .22;
    owner.nextBlink = time + 2.2 + Math.random() * 3.6;
  }
  const k = time - owner.blinkAt, shape = s => s < 0 || s > .21 ? 0 : s < .07 ? rigSmooth(0, .07, s) : s < .1 ? 1 : 1 - rigSmooth(.1, .21, s);
  return Math.max(shape(k), owner.doubleBlink ? shape(k - .28) : 0);
}

// Ink-style effects, drawn in stage units around anchors the choreography chose.
const rigEffects = {
  sparkle(ctx, fx, t) {
    const s = fx.size * (.62 + .38 * Math.abs(Math.sin(t * 5.2 + fx.phase)));
    ctx.save(); ctx.translate(fx.x, fx.y); ctx.rotate(Math.sin(t * 2 + fx.phase) * .2);
    ctx.beginPath(); ctx.moveTo(0, -s);
    ctx.quadraticCurveTo(s * .12, -s * .12, s, 0); ctx.quadraticCurveTo(s * .12, s * .12, 0, s);
    ctx.quadraticCurveTo(-s * .12, s * .12, -s, 0); ctx.quadraticCurveTo(-s * .12, -s * .12, 0, -s);
    ctx.fillStyle = '#ffd54f'; ctx.fill(); ctx.lineWidth = Math.max(1.6, s * .16); ctx.strokeStyle = RIG_INK; ctx.lineJoin = 'round'; ctx.stroke();
    ctx.restore();
  },
  shock(ctx, fx, t) {
    const grow = rigSmooth(0, .14, fx.age), jitter = Math.sin(t * 38 + fx.phase) * 1.2;
    ctx.save(); ctx.translate(fx.x + jitter, fx.y); ctx.lineCap = 'round'; ctx.strokeStyle = RIG_INK; ctx.lineWidth = fx.size * .2;
    for (const k of [-1, 0, 1]) {
      const a = fx.angle + k * .42, r0 = fx.size * .35, r1 = r0 + fx.size * (k ? .8 : 1.05) * grow;
      ctx.beginPath(); ctx.moveTo(Math.cos(a) * r0, Math.sin(a) * r0); ctx.lineTo(Math.cos(a) * r1, Math.sin(a) * r1); ctx.stroke();
    }
    ctx.restore();
  },
  snooze(ctx, fx, t) {
    for (let i = 0; i < 3; i++) {
      const p = ((t * .42 + i / 3) % 1), s = fx.size * (.55 + p * .55);
      const x = fx.x + p * fx.size * 1.6 + Math.sin((t + i) * 2.4) * fx.size * .25, y = fx.y - p * fx.size * 3.2;
      ctx.save(); ctx.globalAlpha *= Math.sin(p * Math.PI); ctx.translate(x, y); ctx.rotate(-.12);
      ctx.beginPath(); ctx.moveTo(-s * .5, -s * .5); ctx.lineTo(s * .5, -s * .5); ctx.lineTo(-s * .5, s * .5); ctx.lineTo(s * .5, s * .5);
      ctx.lineJoin = 'round'; ctx.lineCap = 'round';
      ctx.strokeStyle = '#fffaf0'; ctx.lineWidth = s * .42; ctx.stroke();
      ctx.strokeStyle = '#6f8fb3'; ctx.lineWidth = s * .2; ctx.stroke();
      ctx.restore();
    }
  },
  swish(ctx, fx, t) {
    const beat = Math.sin(t * fx.speed + fx.phase);
    ctx.save(); ctx.translate(fx.x, fx.y); ctx.strokeStyle = RIG_INK; ctx.lineCap = 'round'; ctx.lineWidth = fx.size * .13;
    ctx.globalAlpha *= .35 + .65 * Math.abs(beat);
    for (const k of [0, 1]) {
      const r = fx.size * (1 + k * .45);
      ctx.beginPath(); ctx.arc(0, 0, r, fx.angle - .45 + beat * .12, fx.angle + .45 + beat * .12); ctx.stroke();
    }
    ctx.restore();
  }
};

class IllustratedRig {
  resizeObserver?: ResizeObserver;
  art: any;
  atlas: any;
  blinkAt: any;
  canvas: any;
  cell: any;
  cols: any;
  doubleBlink: any;
  fallback: any;
  frames: any;
  gl: any;
  indexCount: any;
  info: any;
  lastTime: any;
  loading: any;
  mount: any;
  nextBlink: any;
  opacityUniform: any;
  overlay: any;
  overlayCanvas: any;
  popAt: any;
  positionAttribute: any;
  positionBuffer: any;
  positions: any;
  program: any;
  rigSvg: any;
  root: any;
  rows: any;
  small: any;
  stage: any;
  stamp: any;
  texcoords: any;
  texture: any;
  uploaded: any;
  uvAttribute: any;
  uvBuffer: any;

  // art: { stages: {stage: {atlas, height}}, frames: {stage: [cell data]}, choreograph(frame, frames, stage) }
  constructor(mount, art, options: { small?: boolean } = {}) {
    this.mount = mount; this.art = art; this.small = !!options.small;
    this.stage = null; this.atlas = null; this.uploaded = null; this.cell = -1;
    this.popAt = -Infinity; this.blinkAt = -Infinity; this.nextBlink = 1.5 + Math.random() * 2; this.doubleBlink = false;
    this.root = document.createElement('div'); this.root.className = 'illustrated-pet';
    this.root.innerHTML = '<canvas class="pet-canvas"></canvas><canvas class="pet-overlay" aria-hidden="true"></canvas>' +
      '<svg class="illustrated-rig" viewBox="0 0 480 480" aria-hidden="true"></svg><div class="art-loading" role="status" hidden>Waking up a little fluff…</div>';
    mount.append(this.root);
    [this.canvas, this.overlayCanvas] = this.root.querySelectorAll('canvas');
    this.rigSvg = this.root.querySelector('svg'); this.loading = this.root.querySelector('.art-loading');
    this.overlay = this.overlayCanvas.getContext('2d');
    this.canvas.addEventListener('webglcontextlost', event => { event.preventDefault(); this.gl = null; });
    this.canvas.addEventListener('webglcontextrestored', () => { this.initGL(); this.uploaded = null; });
    this.initGL();
    this.resize();
    if (typeof ResizeObserver === 'function') { this.resizeObserver = new ResizeObserver(() => this.resize()); this.resizeObserver.observe(this.root); }
  }
  initGL() {
    const gl = this.canvas.getContext('webgl', { alpha: true, antialias: true, premultipliedAlpha: true });
    this.gl = gl;
    if (!gl) { this.fallback = this.fallback || this.canvas.getContext('2d'); return; }
    const compile = (type, source) => {
      const shader = gl.createShader(type); gl.shaderSource(shader, source); gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw Error(gl.getShaderInfoLog(shader));
      return shader;
    };
    const program = gl.createProgram();
    gl.attachShader(program, compile(gl.VERTEX_SHADER, 'attribute vec2 position;attribute vec2 texcoord;varying vec2 uv;void main(){uv=texcoord;gl_Position=vec4(position.x/240.0-1.0,1.0-position.y/240.0,0.0,1.0);}'));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, 'precision mediump float;uniform sampler2D atlas;uniform float opacity;varying vec2 uv;void main(){gl_FragColor=texture2D(atlas,uv)*opacity;}'));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw Error(gl.getProgramInfoLog(program));
    gl.useProgram(program); this.program = program;
    this.positionAttribute = gl.getAttribLocation(program, 'position'); this.uvAttribute = gl.getAttribLocation(program, 'texcoord');
    this.opacityUniform = gl.getUniformLocation(program, 'opacity');
    gl.uniform1i(gl.getUniformLocation(program, 'atlas'), 0);
    this.cols = 22; this.rows = 26;
    this.positions = new Float32Array((this.cols + 1) * (this.rows + 1) * 2);
    this.texcoords = new Float32Array(this.positions.length);
    const indices = [];
    for (let y = 0; y < this.rows; y++) for (let x = 0; x < this.cols; x++) {
      const a = y * (this.cols + 1) + x, b = a + 1, c = a + this.cols + 1, d = c + 1;
      indices.push(a, b, c, b, d, c);
    }
    this.indexCount = indices.length;
    this.positionBuffer = gl.createBuffer(); this.uvBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, gl.createBuffer()); gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(indices), gl.STATIC_DRAW);
    gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA); gl.clearColor(0, 0, 0, 0);
    this.texture = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, this.texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  }
  dispose(): void { this.resizeObserver?.disconnect(); this.gl?.getExtension("WEBGL_lose_context")?.loseContext(); this.root.remove(); }
  resize() {
    const size = Math.round(Math.min(1400, Math.max(64, (this.root.clientWidth || 340) * Math.min(3, devicePixelRatio || 1))));
    if (this.canvas.width === size && this.canvas.height === size) return;
    this.canvas.width = this.canvas.height = this.overlayCanvas.width = this.overlayCanvas.height = size;
    this.stamp = null;
  }
  setStage(stage) {
    if (this.stage === stage) return;
    const spec = this.art.stages[stage];
    this.stage = stage; this.cell = -1; this.stamp = null;
    this.atlas = loadRigAtlas(spec.atlas); this.frames = this.art.frames[stage];
    this.loading.hidden = this.atlas.ready;
    this.popAt = this.lastTime ?? -Infinity;
  }
  // Maps a cell pixel through the mesh deformation and onto the stage.
  toStage(pose, x, y) {
    const [dx, dy] = rigDeform(pose.ops, x, y), g = pose.global;
    const px = (dx - g.footX) * g.units * g.sx, py = (dy - g.footY) * g.units * g.sy;
    return [g.x + px * g.cos - py * g.sin, g.y + px * g.sin + py * g.cos];
  }
  blinkClosure(time, frame) { return rigBlink(this, time, frame.animate); }
  pose(frame) {
    if (this.stage == null) return null;
    const atlas = this.atlas;
    if (!atlas.ready) { if (atlas.error) { this.loading.hidden = false; this.loading.textContent = atlas.error; } return null; }
    this.loading.hidden = true;
    if (document.hidden) return this.info;
    const time = frame.time; this.lastTime = time;
    const pose = this.art.choreograph(frame, this.frames, this.stage);
    if (pose.cell !== this.cell) { if (this.cell >= 0) this.popAt = time; this.cell = pose.cell; }
    const cell = this.frames[pose.cell];
    // A quick squash-and-settle hides the swap between two drawings.
    const since = time - this.popAt, pop = frame.animate && since >= 0 && since < .45 ? Math.exp(-since / .09) * Math.cos(since * 26) : 0;
    const spec = this.art.stages[this.stage], neutral = this.frames[0];
    const units = spec.height / (neutral.maxY - neutral.minY), squash = (pose.squash ?? 1) * (1 - .085 * pop);
    pose.global = { footX: cell.anchorX, footY: cell.maxY + 1, units, sx: 1 + (1 - squash) * .7, sy: squash,
      x: RIG_STAGE / 2 + (pose.rootX || 0), y: RIG_GROUND - (pose.lift || 0), cos: Math.cos(pose.tilt || 0), sin: Math.sin(pose.tilt || 0) };
    const closure = pose.eyelids ?? (pose.blink === false || !cell.eyes?.length ? 0 : this.blinkClosure(time, frame));
    this.info = { rootX: pose.rootX || 0, lift: Math.max(0, pose.lift || 0), cell: pose.cell, eyelids: closure };
    const stamp = [this.stage, pose.cell, frame.motion, time, frame.showRig, frame.gaze.x.toFixed(3), frame.gaze.y.toFixed(3), this.canvas.width].join(':');
    if (stamp === this.stamp) return this.info;
    this.stamp = stamp;
    if (this.gl) this.drawMesh(pose, cell, frame.showRig);
    else this.drawFlat(pose, cell, frame.showRig);
    this.drawOverlay(pose, cell, closure, frame);
    this.root.dataset.cell = String(pose.cell); this.root.dataset.currentMotion = frame.motion; this.root.dataset.eyelids = closure.toFixed(2);
    this.drawRig(pose, cell, frame.showRig);
    return this.info;
  }
  drawMesh(pose, cell, showRig) {
    const gl = this.gl, scale = this.small ? .5 : 1, texture = paddedRigAtlas(this.atlas, scale);
    gl.useProgram(this.program);
    if (this.uploaded !== texture) {
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, this.texture);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, texture); gl.generateMipmap(gl.TEXTURE_2D);
      this.uploaded = texture;
    }
    // The mesh covers the drawing's bounds (plus a margin), not the whole cell.
    const x0 = Math.max(0, cell.minX - 6), x1 = Math.min(cell.width, cell.maxX + 7);
    const y0 = Math.max(0, cell.minY - 6), y1 = Math.min(cell.height, cell.maxY + 7);
    let i = 0;
    for (let row = 0; row <= this.rows; row++) for (let col = 0; col <= this.cols; col++) {
      const x = x0 + (x1 - x0) * col / this.cols, y = y0 + (y1 - y0) * row / this.rows, [sx, sy] = this.toStage(pose, x, y);
      this.positions[i] = sx; this.positions[i + 1] = sy;
      this.texcoords[i] = (cell.x + x) * scale / texture.width; this.texcoords[i + 1] = (cell.y + y) * scale / texture.height;
      i += 2;
    }
    gl.viewport(0, 0, this.canvas.width, this.canvas.height); gl.clear(gl.COLOR_BUFFER_BIT);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.positionBuffer); gl.bufferData(gl.ARRAY_BUFFER, this.positions, gl.DYNAMIC_DRAW);
    gl.enableVertexAttribArray(this.positionAttribute); gl.vertexAttribPointer(this.positionAttribute, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.uvBuffer); gl.bufferData(gl.ARRAY_BUFFER, this.texcoords, gl.DYNAMIC_DRAW);
    gl.enableVertexAttribArray(this.uvAttribute); gl.vertexAttribPointer(this.uvAttribute, 2, gl.FLOAT, false, 0, 0);
    gl.uniform1f(this.opacityUniform, showRig && !this.small ? .55 : 1);
    gl.drawElements(gl.TRIANGLES, this.indexCount, gl.UNSIGNED_SHORT, 0);
  }
  // Without WebGL: the same drawing and placement, without mesh deformation.
  drawFlat(pose, cell, showRig) {
    const ctx = this.fallback, g = pose.global, k = this.canvas.width / RIG_STAGE;
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.setTransform(k * g.cos * g.units * g.sx, k * g.sin * g.units * g.sx, -k * g.sin * g.units * g.sy, k * g.cos * g.units * g.sy, k * g.x, k * g.y);
    ctx.globalAlpha = showRig && !this.small ? .55 : 1;
    ctx.drawImage(this.atlas.image, cell.x, cell.y, cell.width, cell.height, -g.footX, -g.footY, cell.width, cell.height);
    ctx.globalAlpha = 1;
  }
  drawOverlay(pose, cell, closure, frame) {
    const ctx = this.overlay, k = this.overlayCanvas.width / RIG_STAGE;
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.overlayCanvas.width, this.overlayCanvas.height);
    if (closure > .01) for (const eye of cell.eyes) this.drawEyelid(ctx, k, pose, eye, closure);
    if (pose.panicEyes) for (const eye of cell.eyes) this.drawPanicEye(ctx, k, pose, eye, frame.gaze);
    ctx.setTransform(k, 0, 0, k, 0, 0);
    for (const fx of pose.effects || []) {
      const [x, y] = this.toStage(pose, fx.at[0], fx.at[1]);
      rigEffects[fx.type](ctx, { ...fx, x, y, size: fx.size * pose.global.units }, frame.time);
    }
  }
  // A wide-eyed expression on the flight drawing preserves its spread wings and feather outlines.
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
  // Eyelids in the fur's own colour: the upper lid comes down and the lower lid
  // rises until they meet in a curved ink lash line, like the drawings' closed
  // eyes. Their outer rim fades into the surrounding fur.
  drawEyelid(ctx, k, pose, eye, closure) {
    const grow = 1.14 + 2 / Math.min(eye.rx, eye.ry), inner = 1 / grow;
    const [cx, cy] = this.toStage(pose, eye.x, eye.y), [ax, ay] = this.toStage(pose, eye.x + eye.rx * grow, eye.y), [bx, by] = this.toStage(pose, eye.x, eye.y + eye.ry * grow);
    const m = [(ax - cx) * k, (ay - cy) * k, (bx - cx) * k, (by - cy) * k, cx * k, cy * k];
    const map = (u, v) => [m[0] * u + m[2] * v + m[4], m[1] * u + m[3] * v + m[5]];
    // In the unit space of m the drawn eye has radius `inner`. Each lid edge is
    // the parabola y = edge + sag·(1 − (x/1.1)²), so both lids meet in one curve.
    const meet = .3 * inner, upper = -1 + closure * (1 + meet), lower = 1 - closure * (1 - meet), sag = .42 * inner * closure;
    const fur = eye.fur.join(','), rim = ctx.createRadialGradient(0, 0, inner + .05, 0, 0, 1);
    rim.addColorStop(0, 'rgba(' + fur + ',1)'); rim.addColorStop(1, 'rgba(' + fur + ',0)');
    ctx.save(); ctx.setTransform(...m); ctx.fillStyle = rim; ctx.beginPath();
    ctx.moveTo(-1.1, -1.1); ctx.lineTo(1.1, -1.1); ctx.lineTo(1.1, upper); ctx.quadraticCurveTo(0, upper + 2 * sag, -1.1, upper); ctx.closePath();
    ctx.moveTo(-1.1, lower); ctx.quadraticCurveTo(0, lower + 2 * sag, 1.1, lower); ctx.lineTo(1.1, 1.1); ctx.lineTo(-1.1, 1.1); ctx.closePath();
    ctx.fill(); ctx.restore();
    // The lash line follows the upper lid across the eye. It is stroked in
    // canvas space so its width stays even.
    const y = upper + sag, w = Math.sqrt(Math.max(0, inner * inner - y * y)) * 1.04;
    if (w < .05) return;
    const ends = upper + sag * (1 - (w / 1.1) ** 2), control = ends + 2 * (y - ends);
    const cellPixel = Math.hypot(m[2], m[3]) / (eye.ry * grow);
    ctx.save(); ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.strokeStyle = RIG_INK; ctx.lineCap = 'round';
    ctx.lineWidth = Math.max(1.2, 4.2 * cellPixel * (.6 + .4 * closure));
    ctx.beginPath(); ctx.moveTo(...map(-w, ends)); ctx.quadraticCurveTo(...map(0, control), ...map(w, ends)); ctx.stroke();
    ctx.restore();
  }
  // Show rig: the deformed mesh, the feet anchor, eyes and every operation's pivot.
  drawRig(pose, cell, showRig) {
    this.rigSvg.classList.toggle('visible', showRig);
    if (!showRig) return;
    const lines = [], x0 = cell.minX, x1 = cell.maxX, y0 = cell.minY, y1 = cell.maxY;
    const path = points => 'M' + points.map(p => p.map(v => v.toFixed(1)).join(',')).join('L');
    for (let i = 0; i <= 6; i++) {
      const across = [], down = [];
      for (let j = 0; j <= 20; j++) {
        across.push(this.toStage(pose, x0 + (x1 - x0) * j / 20, y0 + (y1 - y0) * i / 6));
        down.push(this.toStage(pose, x0 + (x1 - x0) * i / 6, y0 + (y1 - y0) * j / 20));
      }
      lines.push('<path d="' + path(across) + '"/><path d="' + path(down) + '"/>');
    }
    const marks = [];
    const mark = (x, y, label) => { const [sx, sy] = this.toStage(pose, x, y); marks.push('<circle cx="' + sx.toFixed(1) + '" cy="' + sy.toFixed(1) + '" r="4"/><text x="' + (sx + 7).toFixed(1) + '" y="' + (sy - 6).toFixed(1) + '">' + label + '</text>'); };
    mark(cell.anchorX, cell.maxY, 'feet');
    for (const eye of cell.eyes || []) mark(eye.x, eye.y, 'eye');
    for (const op of pose.ops) if (op.pivot && op.label) mark(op.pivot[0], op.pivot[1], op.label);
    this.rigSvg.innerHTML = '<g class="mesh-lines">' + lines.join('') + '</g><g class="rig-marks">' + marks.join('') + '</g>';
  }
}

// Shows one renderer per group of growth stages (an egg, then the illustrated bird).
class StagedRenderer implements PetRenderer {
  active: any;
  mount: any;
  parts: any;

  constructor(mount, parts) { this.mount = mount; this.parts = parts.map(part => ({ ...part, host: null, renderer: null })); this.active = null; }
  setStage(stage) {
    for (const part of this.parts) {
      const on = part.stages.includes(stage);
      if (on && !part.renderer) {
        part.host = document.createElement('div'); part.host.className = 'stage-part'; this.mount.append(part.host);
        part.renderer = part.create(part.host);
      }
      if (part.host) part.host.hidden = !on;
      if (on) { part.renderer.setStage(stage); this.active = part.renderer; }
    }
  }
  dispose(): void { for (const p of this.parts) { p.renderer?.dispose?.(); p.host?.remove(); } }
  pose(frame: PetFrame) { return this.active ? this.active.pose(frame) : null; }
}

export { RIG_STAGE, RIG_GROUND, RIG_INK, rigSmooth, rigBlink, rigEffects, loadRigAtlas, IllustratedRig, StagedRenderer };
