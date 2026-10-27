# Adding Damagochi

A new species is a catalog entry, a browser renderer, and any artwork that renderer needs. Growth, saved
progress, permissions, settings, dragging, interactions, and persistence across navigation belong to the shared
Damagochi module. Keep those behaviors in the shared code when adding another Damagochi.

The included Damagochi demonstrate two approaches: **Byte**, the Silkie chicken, uses AI-generated expression sheets
animated by a mesh rig; **Snek**, the galaxy snake, is drawn and animated in code from a supplied character reference.
See [How we used AI](#how-we-used-ai) and the [original artwork prompts](ARTWORK_PROMPTS.md).

## Where things live

Paths below are relative to the repository root.

| File or directory | Purpose |
| --- | --- |
| `DeveAgentManager.Pets/catalog.json` | Species IDs, display/default names, renderer IDs, and four stage labels. Embedded in this assembly. |
| `DeveAgentManager.Pets/PetRules.cs` | Catalog validation, shared growth rules, and persistence models. |
| `DeveAgentManager.Contracts/Pets/PetTypes.cs` | API contracts shared with the Portal. |
| `DeveAgentManager.Core/Pets/PetService.cs` | Saved state, token attribution, interactions, and manager controls. |
| `DeveAgentManager/Browser/pet-host.ts` | Loads renderers, supplies animation frames, handles dragging and browser lifecycle. |
| `DeveAgentManager/Browser/pets/` | Renderer implementations, shared egg, rig, and TypeScript contracts. |
| `DeveAgentManager/wwwroot/pets/` | Published artwork and frame metadata. |
| `DeveAgentManager.Client/Shared/PetHost.razor` | Persistent UI host outside the page router. |
| `DeveAgentManager.Tests/Browser/pets.mts` | Browser regression checks for the included Damagochi. |

For the feature's settings and token-accounting behavior, see [Damagochi](../docs/damagochi.md).

## 1. Register the species

Append an object to [catalog.json](catalog.json), keeping the surrounding JSON array valid. For example:

```json
{
  "id": "fennec",
  "name": "Fennec fox",
  "defaultName": "",
  "renderer": "fennec-art",
  "stages": ["Egg", "Kit", "Young", "Adult"]
}
```

| Field | Meaning |
| --- | --- |
| `id` | Unique, stable species key saved in user accounts. Use a lowercase slug; renaming an existing key requires migrating saved Damagochi. |
| `name` | Species label in the settings picker. |
| `defaultName` | Retained catalog field; leave it empty. Users explicitly name their Damagochi when adopting it. |
| `renderer` | Module filename without `.ts` or `.js`. It must match `^[a-z][a-z0-9-]*$`; paths and extensions are rejected by the host. |
| `stages` | Exactly four labels in the order below. Labels do not change the growth thresholds. |

| Stage passed to the renderer | Levels | Role |
| --- | --- | --- |
| `0` | No level | Egg-to-Damagochi transition when a user chooses and names a Damagochi. The unchosen egg has its own shared renderer. |
| `1` | 1–5 | Newly hatched Damagochi. |
| `2` | 6–9 | Young Damagochi. |
| `3` | 10+ | Adult. |

The catalog feeds the existing settings picker and API. Adding a species normally needs no new endpoint,
authorization rule, database migration, or settings component. Users choose a species once; replacement requires the
confirmed Kill Damagochi action in My settings → Damagochi and resets that Damagochi's XP. It is absent from the
right-click menu. Account tokens survive; the current Damagochi's lifetime tokens reset. Damagochi managers can change species
directly while retaining progress. New names and species start empty until the user completes adoption.

All species share `PetRules.GrowthScale`: level N begins at `25,000,000 × (N − 1) × N` tokens. Young begins at
750 million and adult at 2.25 billion tokens. Keep growth out of individual renderers. Changing this curve preserves
saved token balances and recalculates levels/stages; it does not reset account usage. The blue Account usage bar is
separate from species renderers and XP, stays visible for an egg, and continues counting when Damagochi is disabled.

## 2. Implement the renderer

Create `DeveAgentManager/Browser/pets/fennec-art.ts`. Start from
[snake-art.ts](../DeveAgentManager/Browser/pets/snake-art.ts) for a drawing made in code, or
[silkie-art.ts](../DeveAgentManager/Browser/pets/silkie-art.ts) for expression sheets and choreography.
The contract is [types.ts](../DeveAgentManager/Browser/pets/types.ts):

```ts
import type { PetRenderer, RendererOptions } from "./types.js";

export function createRenderer(mount: HTMLElement, _options: RendererOptions): PetRenderer {
    const canvas = document.createElement("canvas");
    canvas.className = "pet-canvas";
    canvas.width = canvas.height = 480;
    canvas.style.width = canvas.style.height = "100%";
    mount.append(canvas);
    const context = canvas.getContext("2d");
    if (!context) { canvas.remove(); throw new Error("Canvas is unavailable."); }
    const ctx = context;
    let stage = 0;

    return {
        setStage(value) { stage = value; },
        pose(frame) {
            ctx.clearRect(0, 0, 480, 480);
            const lift = frame.animate && frame.motion === "hop"
                ? Math.abs(Math.sin(frame.elapsed * 6)) * 20 : 0;

            // Placeholder: replace this shape with the species' egg/body and expressions.
            // It demonstrates stage changes and a motion without starting another render loop.
            ctx.strokeStyle = getComputedStyle(mount).color;
            ctx.lineWidth = 6;
            ctx.beginPath();
            ctx.ellipse(240, 350 - lift, 40 + stage * 14, 55 + stage * 16, 0, 0, Math.PI * 2);
            ctx.stroke();
            return { rootX: 0, lift };
        },
        dispose() { canvas.remove(); }
    };
}
```

Replace the placeholder drawing before registering the finished species. A renderer may use Canvas 2D, SVG,
or WebGL; its anatomy and drawing method are its own. The existing renderers use a 480 × 480 logical stage.
The illustrated rig grounds the feet at `RIG_GROUND` (442).

An optional exported `async prepare(): Promise<void>` loads frame metadata and images. The host awaits it before
creating the renderer and caches the prepared module. Await image decoding/loading as well as JSON: reduced-motion
mode may draw only one frame. Loading images after that frame can leave an otherwise valid Damagochi blank.

### Frames, motions, and cleanup

| `PetFrame` field | Meaning |
| --- | --- |
| `time` | Animation time in seconds. |
| `elapsed` | Seconds since the current motion began. |
| `motion` | Requested expression/action. Use idle as a fallback for unknown values. |
| `gaze` | Pointer direction, with `x` and `y` clamped to −1…1. |
| `animate` | Whether movement is allowed; render a complete still pose when false. |
| `progress` | Progress through the current growth stage, 0…1. Used for egg cracks. |
| `transitioning` | Whether the host is playing the egg-to-Damagochi transition. |
| `showRig` | Debug drawing flag; the Portal currently supplies false. |

Support `idle`, `happy`, `wave`, `hop`, `peck`, `sleep`, `sad`, `surprised`, and `hatch`. The host occasionally chooses
`peck`, `walk`, `wave` or `hop` during idle. `sad` also previews crying while hovering/focusing Kill Damagochi; `sleep`
precedes the retirement fade. Go away actions use `sad`, then `sleep`, then a shared fade, including drag-to-hide
and indefinite hiding. Reduced motion uses still poses. These are animation states, not a required number of image files: several
motions can reuse an expression with different movement. The host translates `working` into `idle` and `level` into
`happy`. Affection uses `happy`, play uses `hop`, image uploads use `surprised`, and other uploads use `peck`.

Use `frame.animate` to stop breathing, blinks, wobble, and similar movement. Respect the supplied frame instead of
starting your own `requestAnimationFrame`, interval, token counter, API polling, or navigation listener. The host owns
the loop, pauses it for hidden tabs and hidden Damagochi, and preserves the renderer through internal navigation.

Implement `dispose()` whenever you allocate observers, event handlers, DOM nodes, or graphics resources. The host
calls it when changing renderer or disposing the host. Return `{ rootX, lift }` as pose metadata, or `null` when there
is no pose yet; the current host leaves placement to the renderer.

Use [StagedRenderer and IllustratedRig](../DeveAgentManager/Browser/pets/illustrated-rig.ts) to compose different
renderers for the egg and body. [InkEgg](../DeveAgentManager/Browser/pets/ink-egg.ts) accepts a species-specific shell,
markings, colors, and crack geometry.

## 3. Prepare and load artwork

For a renderer with its own asset loader, put files under `DeveAgentManager/wwwroot/pets/<species-id>/`. From a
renderer module, a metadata URL can be written as:

```ts
const response = await fetch(new URL("../../pets/fennec/frames.json", import.meta.url));
if (!response.ok) throw new Error("Could not load the pet artwork.");
```

TypeScript emits the module into `wwwroot/js/pets/`, so the URL resolves to `/pets/fennec/frames.json` at runtime.
Import sibling modules with `.js` extensions in TypeScript, as the existing renderers do.

**Atlas loader convention:** the existing `loadRigAtlas(id)` helper resolves IDs to
`wwwroot/pets/silkie/<id>.webp`. Reusing that helper unchanged requires placing the uniquely named atlases there.
To use another asset directory with the shared rig, first make its loader accept an explicit asset location and
update Byte's callers, or provide a renderer with its own loader. A new catalog entry does not change that path.

Byte has one transparent WebP sheet for each body stage and a shared `frames.json`. Each sheet has twelve
expressions, read left to right and then top to bottom:

| Row | Column 1 | Column 2 | Column 3 | Column 4 |
| --- | --- | --- | --- | --- |
| 1 | `0` idle | `1` blink | `2` happy | `3` wave |
| 2 | `4` sad | `5` shocked | `6` sleep | `7` peck |
| 3 | `8` walk left | `9` walk right | `10` hop | `11` cheer |

This order is Byte's convention, defined by `SILKIE_CELL`, rather than a global renderer requirement. If you copy
its choreography, keep the cell order consistent across all three body stages. The egg is drawn separately.

For `IllustratedRig`, each stage supplies an atlas ID and a logical height, plus frame records:

| Metadata | Meaning |
| --- | --- |
| `cell` | Index used by the choreography. |
| `x`, `y`, `width`, `height` | Cell rectangle in the atlas. |
| `minX`, `minY`, `maxX`, `maxY` | Visible drawing bounds, in pixels relative to the cell. |
| `anchorX` | Horizontal foot/body anchor, relative to the cell; vertical grounding uses `maxY + 1`. |
| `eyes` | Verified eye centers/radii (`x`, `y`, `rx`, `ry`) and surrounding color (`fur`, RGB) for procedural eyelids. Use an empty array for unsuitable expressions. |

Read [Byte's metadata](../DeveAgentManager/wwwroot/pets/silkie/frames.json) alongside its renderer. Wing/head pivots and
deformation regions live in the renderer's choreography; metadata alone does not animate a new animal correctly.
Measure those anchors against the new artwork. Keep transparent margins around ears, wings, tails, and raised limbs.

## How we used AI

We developed the Damagochi in a standalone HTML playground before integrating the approved renderers into the Manager.
AI assisted both artwork iteration and implementation, with the supplied character references and user feedback
deciding the result.

### Byte: generated drawings, then a rig

1. The original Silkie character sheet supplied the identity and illustration style. The built-in image-generation
   tool produced an adult sheet with a genuine alpha background and twelve specified expressions in a 4 × 3 grid.
2. The adult artwork became the reference for younger variants. A further chick redesign explicitly requested a
   squat round body, large eyes, three short crest tufts, and very few markings. Distinct baby proportions needed
   stronger instructions than asking for a smaller adult. The [original prompts](ARTWORK_PROMPTS.md) preserve this
   sequence, including the intermediate prompt labeled `young`.
3. A prototype atlas-analysis script removed stray specks and pale edge fringes, found the actual cell boundaries
   despite uneven generated spacing, measured visible bounds and foot anchors, and detected candidate eyes.
4. Debug contact sheets were reviewed by eye. Only verified eye detections were retained for blinking; wing pivots
   were measured manually. The processor's cream-feather/brown-eye heuristics were specific to this artwork.
5. Cleaned sheets became WebP files and frame metadata. The browser rig adds breathing, blinks, head/wing/foot motion,
   squash, jumps, and effects. The generated sheets provide expression drawings rather than a finished video.

The original processor ran in the prototype. This repository contains its prepared outputs; the
`generated: "scripts/analyze-atlas.cjs"` field in Byte's metadata records that provenance. For another species,
prepare equivalent frame metadata with an adapted processor or measured cells and anchors. Verify the results
against the new animal's colors and anatomy.

### Snek: a reference translated into code

Snek's body follows a sampled spine and is painted in Canvas 2D. Its face, mouth, tongue, markings, and rattle are
drawn in code from the supplied reference. User reviews refined the face while keeping the liked body, including
the correction that the mouth should open from the lower jaw. This route allows coiling, slithering, and face
adjustments through geometry and choreography rather than replacing sprite sheets.

### A repeatable AI workflow for the next Damagochi

Start with an approved reference and neutral adult pose, then derive the growth stages and expressions from that
same reference. Keep the face, markings, outline style, proportions within each stage, and palette consistent.
Review a browser preview at the Damagochi's actual display size after each meaningful change.

For an atlas-based Damagochi, this is a starting prompt template, adapted from Byte's prompts:

```text
Use the attached approved [species] character as the identity and style reference.
Create the [adult / young / hatchling] stage of the SAME character.
Preserve [face, distinctive markings, colors, line style].
For the younger stage, use [specific age-appropriate proportions and features].

Create one transparent expression atlas: four columns by three rows, ideally 2048 × 1536.
Use real alpha transparency, with no background, checkerboard, text, labels, grid, or shadows.
One complete character per cell, consistent size and foot baseline, with 10–12% empty margins.
Only the pose and expression change within this stage.

Row 1: idle, blink, happy, wave.
Row 2: sad, shocked, sleep, peck.
Row 3: walk left, walk right, hop, cheer.
Keep limbs, ears, and tails fully inside their cells. Add effects such as sparkles in code.
```

Enable transparent output in the image tool as well as requesting it in the prompt. Inspect the resulting alpha,
cell spacing, cropping, anatomy, and consistency; the prompt's requested grid and dimensions need verification.
For a targeted repair, reference the approved image and describe exactly what should change and what should stay.
Keep the original references, prompts, generation settings/tool version when available, and processing notes with
the new Damagochi's development assets. Add a prompt record like [ARTWORK_PROMPTS.md](ARTWORK_PROMPTS.md) for future edits.

AI is used during development. The shipped Damagochi render locally, and Damagochi growth reads recorded usage from users'
normal agent work. The Damagochi module makes no model calls of its own.

## 4. Build and validate

Run commands from the repository root with .NET 10, Node 22+, and npm available:

```sh
npm ci --ignore-scripts
npm run check
dotnet build DeveAgentManager.Tests -c Release
dotnet DeveAgentManager.Tests/bin/Release/net10.0/DeveAgentManager.Tests.dll -noLogo -class '*PetTests' -class '*PetApiTests' -class '*PetMigrationTests'
```

`dotnet build` also compiles the browser TypeScript. Edit `.ts` sources, then rebuild; emitted files in `wwwroot/js`
and `.typescript` are generated and should stay out of commits.

To run the CI interop suite, including the existing Damagochi browser checks, against a disposable Manager:

```sh
npm ci --prefix tools/security-browser --ignore-scripts
npx --prefix tools/security-browser playwright install --with-deps chromium
dotnet publish DeveAgentManager/DeveAgentManager.csproj -c Release -o .validation/publish
node .typescript/tools/security-browser/continuations.mjs interop
```

The wrapper creates its own temporary database and test-only login, starts a local Manager, and cleans it up.
It needs local port 19081 available. The Damagochi test changes global settings and user Damagochi, so use an isolated
development instance for manual checks too.

The current browser test explicitly exercises Snek and Byte. Extend it for the new species rather than assuming a
catalog entry is covered automatically. With a Damagochi manager account, enable the module, select the new species,
and choose **Unchosen egg** for the waiting state. Adopt a named Damagochi to inspect its hatch transition, then set levels
**1, 6, and 10** to inspect its three body stages. Check:

- Every expression has the right face, stable proportions, intact extremities, and a consistent ground anchor.
- Adoption starts at zero XP, the hatch transition works, and the chosen name is visible.
- Kill Damagochi previews crying, requires confirmation, sleeps/fades into an unchosen egg, resets Damagochi lifetime
  tokens and preserves the separate Account usage bar.
- Administrator species changes preserve Damagochi progress; user replacement starts a fresh Damagochi.
- Navigation between normal pages and the terminal retains the same renderer/canvas for the selected species.
- Dragging, context menu, touch hold, snooze/wake, and hiding/restoring work at desktop and phone sizes.
- Reduced motion draws a complete still pose after a cold asset load; light/dark themes both keep edges clean.
- An atlas renderer has a usable Canvas 2D fallback when WebGL is unavailable, and switching species releases resources.
- Cold loads produce no missing-module, missing-asset, or unhandled browser errors.

Commit the catalog entry, renderer source, artwork/metadata, prompt record, and relevant test changes together.
