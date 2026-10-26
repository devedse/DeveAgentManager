import { InkEgg } from "./ink-egg.js";
import type { PetRenderer, RendererOptions } from "./types.js";

// An unchosen egg has no species identity. Its question mark matches the setup action below it.
export function createRenderer(mount: HTMLElement, _options: RendererOptions): PetRenderer {
    return new InkEgg(mount, {
        shape: "M0,-225C54,-225 94,-136 94,-83C94,-30 53,1 0,1C-53,1-94,-30-94,-83C-94,-136-54,-225 0,-225Z",
        weight: "M91,-63C79,-22 47,1 0,1C-34,1-63,-13-82,-37",
        crack: [[-94,-112],[-68,-99],[-48,-121],[-24,-100],[0,-120],[24,-99],[48,-121],[72,-100],[94,-114]],
        smallCrack: "M24,-208L34,-190L23,-176", sideCrack: "M-94,-112L-68,-99L-48,-121",
        shell: ["#fffdf3", "#f4e9d1", "#d5c3a3"],
        markings: '<text x="0" y="-82" text-anchor="middle" font-family="system-ui,sans-serif" font-size="76" font-weight="700" fill="#625645">?</text>'
    });
}
