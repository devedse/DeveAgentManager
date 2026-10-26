# Damagochi

Damagochi is an optional pet module. A Damagochi manager enables it under Manager settings → Damagochi.
Admin includes this role. Damagochi managers can manage pets without access to account security or conversations.

The global switch preserves individual preferences. The new-user default is copied when an account is created.
Enable/disable everyone changes existing users’ switches. Users can change their own switch while the module is
available; Damagochi managers can edit preferences while it is disabled. No action deletes earned progress.

Each person has one pet across their agents and workspaces. The pet host lives beside the Portal router, above both
layouts, so internal navigation preserves its renderer. My settings → Damagochi controls the pet type, name, size and
reduced movement. The context menu offers affection, play, snooze and indefinite hiding. Dragging saves a position
per user in that browser; keyboard arrows move it too. Shift+F10 opens its menu. A touch hold opens the same menu.

## Growth and usage

Level N starts at `25,000 × (N − 1) × N` tokens. Levels 1–2 are the egg, 3–5 the hatchling, 6–9 young, and 10+ adult.
The maximum level is 1000. Changing species preserves experience. Administrator level adjustments move experience
to the start of the chosen level. Happiness ranges from 0–100; affection has a cooldown and low happiness changes
the idle expression. Snoozing still earns progress; disabling closes the current turn’s growth eligibility; new prompts earn growth after re-enabling. Administrator changes are audited.

Usage summaries are collected incrementally before history metadata is compacted. They are stored separately from
the small list-summary manifest; unchanged generations are not reread for progression. Claude streaming messages are
counted by message id; pi uses recorded message usage. Codex cumulative token counts are converted into deltas;
its cached input and reasoning totals are subsets and are not counted twice. Harnesses without recorded usage do
not receive guessed tokens. The pet feature performs no model calls.

A terminal submission records only an attribution candidate: user, session and time, never prompt text. A matching
real user message and recorded token usage are required to award experience. Unattributed work, imported history,
and old work without a matching submission earn no experience. Usage high-water marks keyed by native source and
turn survive restarts, session copies and navigation. Browser tabs cannot award experience. Work submitted directly
over SSH is currently unattributed. Candidates are retained for seven days.

## Adding a species

1. Add an entry to `DeveAgentManager.Pets/catalog.json`: unique id, display name, default name, renderer module and
   four stage labels. Catalog definitions are embedded in the module and returned by the pet API.
2. Add `DeveAgentManager/Browser/pets/<renderer>.ts`. Export `createRenderer(mount, options)` returning a `PetRenderer`
   from `types.ts`. An optional async `prepare()` loads that species’ assets once. Keep assets in `wwwroot/pets/`.
3. Implement `setStage(0..3)` and `pose(PetFrame)`. Support idle, happy, wave, hop, walk, peck, sleep, sad, surprised and
   hatch. Supply `dispose()` to release observers and graphics resources. Use the shared egg or illustrated rig when
   appropriate. The host loads the selected module only when a pet becomes visible.
4. Check the new species at each growth stage, at phone sizes, with reduced motion and without WebGL. No progression,
   authorization, API or settings component changes are needed for another species.

Progression and catalog rules live in `DeveAgentManager.Pets`; the Core pet service supplies persistence and Manager
activity adapters. Server state uses the existing database and live-update connection. Browser code handles drawing
and pointer interactions. Disabling the module stops rendering while retaining saved pet state.

## Validation

`PetTests` covers progression, deduplicated usage, defaults, snoozing and persistence. `PetApiTests` covers independent
role permissions and API request protection. Run `DeveAgentManager.Tests/Browser/pets.mts` against an isolated preview
Manager; it changes that Manager’s pet settings and checks navigation, interaction and both included pets.
