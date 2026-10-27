# Damagochi

Damagochi is an optional companion module. A Damagochi manager enables it under Manager settings → Damagochi.
Admin includes this role. Damagochi managers can manage Damagochi without access to account security or conversations.

The global switch preserves individual preferences. The new-user default is copied when an account is created.
Enable/disable everyone changes existing users’ switches. Users can change their own switch while the module is
available; Damagochi managers can edit preferences while it is disabled. No action deletes earned progress.

Each person has one Damagochi across their agents and workspaces. The Damagochi host lives beside the Portal router, above both
layouts, so internal navigation preserves its renderer. My settings → Damagochi controls the Damagochi type, name, size and
reduced movement. The context menu offers affection, play, snooze and indefinite hiding. Dragging saves a position
per user in that browser; keyboard arrows move it too. Shift+F10 opens its menu. A touch hold opens the same menu.
The context menu includes current-level XP and lifetime tokens. Enabled accounts without a Damagochi show an unchosen egg
with a question mark and a “Choose Damagochi & name” action. Species and name start empty. Choosing both hatches a level-1 Damagochi.

## Growth and usage

Level N starts at `25,000,000 × (N − 1) × N` Damagochi tokens, fifty times the previous curve. Levels 1–5 are the
hatchling, 6–9 young (750 million tokens), and 10+ adult (2.25 billion tokens). At 32 million tokens per hour this
means about 70 active hours to adulthood; actual time depends on usage. The unchosen egg has no level. The maximum
level is 1000. Existing token balances are preserved and levels/stages are recalculated with these thresholds.
Each Damagochi has its own tokens/XP, separate from remembered lifetime tokens. Choosing a Damagochi starts its XP at zero;
tokens credited while choosing an egg increase lifetime usage only. Killing a Damagochi requires confirmation, resets
its XP, level and happiness, and returns to an unnamed, unchosen egg. Lifetime usage survives. Delayed usage from an
older Damagochi generation still counts toward lifetime usage while enabled, but cannot grow a replacement Damagochi.

Users can rename their Damagochi; changing species requires the “Kill Damagochi and create new one” flow, available
only in My settings → Damagochi. The right-click menu links to settings and has no kill action. Hovering or focusing
the settings button previews crying locally. Confirmation plays sleep, then a fade, then the unchosen egg. Reduced motion
uses a shorter still-pose fade. Hidden tabs pause animation; ordinary idle includes occasional pecking, walks and waves.
Damagochi managers can directly change species, name, happiness, level, exact Damagochi tokens and lifetime tokens, or choose
an unchosen egg. Manager species changes keep current progress unless explicitly adjusted. Level adjustments move
experience to the start of the chosen level. Existing token balances survive migration and initialize lifetime usage;
levels are recalculated with the slower curve. Untouched, disabled, zero-token default Damagochi become unchosen eggs.
Happiness ranges from 0–100; affection has a cooldown and low happiness changes
the idle expression. Snoozing still earns progress; disabling closes the current turn’s growth eligibility; new prompts earn growth after re-enabling. Administrator changes are audited.

Usage summaries are collected incrementally before history metadata is compacted. They are stored separately from
the small list-summary manifest; unchanged generations are not reread for progression. Claude streaming messages are
counted by message id; pi uses recorded message usage. Codex cumulative token counts are converted into deltas;
its cached input and reasoning totals are subsets and are not counted twice. Harnesses without recorded usage do
not receive guessed tokens. The Damagochi feature performs no model calls.

The XP bar shows only progress within the current level. Total Damagochi tokens include the XP spent reaching
earlier levels. For example, 64,203,424 total tokens now means level 2 with 14,203,424 / 100,000,000 XP toward level 3.
Lifetime tokens retain usage across Damagochi resets and while waiting to choose an egg. When lifetime tracking was
introduced, it was initialized from the existing Damagochi XP balance; earlier account usage was not imported.
Counts include recorded input, output and cached context, so they can greatly exceed the words typed or displayed.
The shared XP component explains these counters in its help tip.

A terminal submission records only an attribution candidate: user, session and time, never prompt text. A matching
real user message and recorded token usage are required to award experience. Unattributed work, imported history,
and old work without a matching submission earn no experience. Usage high-water marks keyed by native source and
turn survive restarts, session copies and navigation. Browser tabs cannot award experience. Work submitted directly
over SSH is currently unattributed. Candidates are retained for seven days.

## Adding a species

See the [Damagochi project README](../DeveAgentManager.Pets/README.md) for a full walkthrough, renderer examples, artwork
requirements, the AI workflow and original prompts, and validation commands.

1. Add an entry to `DeveAgentManager.Pets/catalog.json`: unique id, display name, default name, renderer module and
   four stage labels. Catalog definitions are embedded in the module and returned by the Damagochi API.
2. Add `DeveAgentManager/Browser/pets/<renderer>.ts`. Export `createRenderer(mount, options)` returning a `PetRenderer`
   from `types.ts`. An optional async `prepare()` loads that species’ assets once. Keep assets in `wwwroot/pets/`.
3. Implement `setStage(0..3)` and `pose(PetFrame)`. Stage 0 is the adoption hatch transition; the unchosen egg uses a
   shared renderer. Support idle, happy, wave, hop, walk, peck, sleep, sad (including the Kill Damagochi hover), surprised and
   hatch. Supply `dispose()` to release observers and graphics resources. Use the shared egg or illustrated rig when
   appropriate. The host loads the selected module only when a Damagochi becomes visible.
4. Check the new species at each growth stage, at phone sizes, with reduced motion and without WebGL. No progression,
   authorization, API or settings component changes are needed for another species.

Progression and catalog rules live in `DeveAgentManager.Pets`; the Core Damagochi service supplies persistence and Manager
activity adapters. Server state uses the existing database and live-update connection. Browser code handles drawing
and pointer interactions. Disabling the module stops rendering while retaining saved Damagochi state.

## Validation

`PetTests` covers progression, deduplicated usage, defaults, snoozing and persistence. `PetApiTests` covers independent
role permissions and API request protection. Run `DeveAgentManager.Tests/Browser/pets.mts` against an isolated preview
Manager; it changes that Manager’s Damagochi settings and checks navigation, interaction and both included Damagochi.
