import { RIG_INK, rigSmooth } from "./illustrated-rig.js";
/* An egg in the ink style, shared by every pet. A species describes its shell
 * (outline, colours, markings and crack line); the egg rocks, wiggles, cracks
 * as the tokens add up, and splits open when the pet hatches.
 * Coordinates: the egg stands on (0, 0), the bottom of the 480 × 480 stage. */
let inkEggCount = 0;
function inkEggDefs(id, spec) {
  const crack = spec.crack.map(p => p.join(',')).join(' '), [c0, c1, c2] = spec.shell;
  const body = '<g id="' + id + '-body"><g clip-path="url(#' + id + '-clip)">' +
    '<path d="' + spec.shape + '" fill="url(#' + id + '-shell)"/>' + spec.markings +
    '</g><path d="' + spec.shape + '" fill="none" stroke="' + RIG_INK + '" stroke-width="5.5" stroke-linejoin="round"/>' +
    '<path d="' + spec.weight + '" fill="none" stroke="' + RIG_INK + '" stroke-width="8.5" stroke-linecap="round"/></g>';
  return '<defs><radialGradient id="' + id + '-shell" cx="-28" cy="-140" r="190" gradientUnits="userSpaceOnUse">' +
    '<stop offset="0" stop-color="' + c0 + '"/><stop offset=".55" stop-color="' + c1 + '"/><stop offset="1" stop-color="' + c2 + '"/></radialGradient>' +
    '<clipPath id="' + id + '-clip"><path d="' + spec.shape + '"/></clipPath>' +
    '<clipPath id="' + id + '-top"><polygon points="-110,-112 ' + crack + ' 110,-114 110,-260 -110,-260"/></clipPath>' +
    '<clipPath id="' + id + '-bottom"><polygon points="-110,-112 ' + crack + ' 110,-114 110,40 -110,40"/></clipPath>' +
    body + '</defs>';
}
class InkEgg {
  parts: any;
  rig: any;
  svg: any;

  constructor(mount, spec) {
    const id = 'ink-egg-' + (++inkEggCount), crack = 'M' + spec.crack.map(p => p.join(',')).join('L');
    mount.innerHTML = '<svg class="egg-svg" viewBox="0 0 480 480" aria-hidden="true">' + inkEggDefs(id, spec) +
      '<g transform="translate(240 442)"><g data-egg="root">' +
      '<use href="#' + id + '-body" data-egg="whole"/>' +
      '<g data-egg="split" display="none"><use href="#' + id + '-body" clip-path="url(#' + id + '-bottom)"/>' +
      '<path d="' + crack + '" fill="none" stroke="' + RIG_INK + '" stroke-width="4" stroke-linejoin="round"/>' +
      '<g data-egg="lid"><use href="#' + id + '-body" clip-path="url(#' + id + '-top)"/><path d="' + crack + '" fill="none" stroke="' + RIG_INK + '" stroke-width="4" stroke-linejoin="round"/></g></g>' +
      '<g fill="none" stroke="' + RIG_INK + '" stroke-linejoin="round" stroke-linecap="round">' +
      '<path data-egg="crack-small" d="' + spec.smallCrack + '" stroke-width="3" display="none"/>' +
      '<path data-egg="crack-side" d="' + spec.sideCrack + '" stroke-width="3" display="none"/>' +
      '<path data-egg="crack-line" d="' + crack + '" stroke-width="3.5" pathLength="1" stroke-dasharray="1 1" display="none"/></g>' +
      '<g class="egg-rig" display="none"><path d="' + spec.shape + '" fill="none" stroke="#bc6c8c" stroke-dasharray="5 5"/>' +
      '<circle r="5" fill="#fff8ea" stroke="#a8406c" stroke-width="1.6"/><text x="9" y="-6">rock</text><circle cy="-100" r="4" fill="#fff8ea" stroke="#a8406c" stroke-width="1.6"/><text x="9" y="-106">shell</text></g>' +
      '</g></g></svg>';
    this.svg = mount.firstElementChild;
    this.parts = Object.fromEntries([...this.svg.querySelectorAll('[data-egg]')].map(el => [el.dataset.egg, el]));
    this.rig = this.svg.querySelector('.egg-rig');
  }
  setStage() {}
  pose(frame) {
    const { motion: m, elapsed: e, time: t } = frame, live = frame.animate ? 1 : 0;
    const hatching = frame.transitioning && m === 'hatch';
    let tilt = live * Math.sin(t * 1.2) * 1.5, lift = 0, breathe = live * Math.sin(t * 2) * .006;
    if (hatching) {
      const k = Math.min(1, e / .7);
      tilt = Math.sin(e * 18) * 7 * k; lift = Math.abs(Math.sin(e * 9)) * 4 * k;
    } else if (m !== 'idle' && m !== 'sleep') {
      const envelope = Math.max(0, 1 - e / 1.6);
      tilt = Math.sin(e * 15) * 6.5 * envelope; lift = Math.abs(Math.sin(e * 7.5)) * 5 * envelope;
    }
    this.parts.root.setAttribute('transform', 'translate(0 ' + (-lift).toFixed(2) + ') rotate(' + tilt.toFixed(2) + ') scale(' + (1 - breathe * .6).toFixed(4) + ' ' + (1 + breathe).toFixed(4) + ')');
    // Cracks appear as the egg gets closer to hatching.
    const progress = frame.progress ?? 0, split = hatching && e > .7;
    this.parts['crack-small'].setAttribute('display', (progress >= .35 || hatching) && !split ? 'inline' : 'none');
    this.parts['crack-side'].setAttribute('display', progress >= .7 && !hatching ? 'inline' : 'none');
    this.parts['crack-line'].setAttribute('display', hatching && !split ? 'inline' : 'none');
    this.parts['crack-line'].setAttribute('stroke-dashoffset', hatching ? String(1 - Math.min(1, e / .65)) : '1');
    this.parts.whole.setAttribute('display', split ? 'none' : 'inline');
    this.parts.split.setAttribute('display', split ? 'inline' : 'none');
    if (split) {
      const p = rigSmooth(.7, 1.05, e);
      this.parts.lid.setAttribute('transform', 'translate(' + (-14 * p).toFixed(1) + ' ' + (-70 * p).toFixed(1) + ') rotate(' + (-28 * p).toFixed(1) + ' -40 -110)');
    }
    this.rig.setAttribute('display', frame.showRig ? 'inline' : 'none');
    return { rootX: 0, lift };
  }
}
function inkEggIcon(spec) {
  const id = 'ink-egg-icon-' + (++inkEggCount), wrap = document.createElement('span');
  wrap.innerHTML = '<svg viewBox="-100 -215 200 230" aria-hidden="true">' + inkEggDefs(id, spec) + '<use href="#' + id + '-body"/></svg>';
  return wrap.firstElementChild;
}

export { InkEgg, inkEggIcon };
