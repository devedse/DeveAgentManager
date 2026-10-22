# Notes for agents working on DeveAgentManager

## Background verification

Use the harness’s built-in background tasks whenever it supports them. Use `dam-tools run-job`
only when the harness has no built-in background-job support; a long runtime, restart survival or
validation needs do not override this rule. See [docs/background-jobs.md](docs/background-jobs.md).
Keep job ownership, strict completion validation and keyed terminal delivery
separate. Never replay a workload after transport loss or an ambiguous delivery.
Controller outbox checks do not require recurring agent turns. Record live
deployment/wake verification separately from host test success.

## Delivering changes

When the user requests changes, commit and push them to `master` as soon as they are ready. If the user explicitly
requests work on a branch, commit and push to that branch instead. Do not leave requested changes only in the local
working tree or ask for separate permission to commit and push.

## Usage-limit continuations

A passed reset is permission to retry, never proof that the agent resumed. Keep a current rejection and its send
result visible until a healthy live observation establishes recovery. A newer rejection replaces an automatic
schedule; explicit one-time schedules, drafts and uncertain sends retain their protections. Anchor clock-only resets
to the original observation, and never resend merely because the same error remains visible. Keep enabled-policy
state separate from whether continuation can currently proceed. See [docs/usage-limit-continuations.md](docs/usage-limit-continuations.md)
for the regression matrix and diagnostics required when changing this flow.

## UI: explanations go in help tips

Keep pages short. A heading or field gets at most one short line of visible text; anything longer goes behind a (?)
help button next to it.

- **Portal pages** (`DeveAgentManager.Client`): `<HelpTip Label="…">…</HelpTip>`
  (`DeveAgentManager.Client/Shared/HelpTip.razor`). Put the heading and tip in a `<div class="help-heading">`.
- **The Manager's own static pages** (sign-in, change password, account security in
  `DeveAgentManager/Components/Pages`, which run no Blazor in the browser): `<StaticHelpTip Label="…">…</StaticHelpTip>`
  (`DeveAgentManager/Components/Shared/StaticHelpTip.razor`), a native popover with the same look.
- Write help as plain paragraphs (`<p>`): what it does, what happens when you use it, and anything that can't be undone.
- Everything follows the light/dark theme: use the `--dam-*` CSS variables in `wwwroot/app.css`, not fixed colours. (Pages
  on VS Code's own hostname, like its loading page in `EditorEndpoints.cs`, can't load `app.css` and copy the values.)

## UI: loading states

Pages load their data over the network, which is slow on phones. Never show an empty state, a zero, a count or "not
found" before the data has loaded: show that it is loading instead, where the content will appear. (Based on NN/g and
GitHub Primer's loading guidance.)

- **Per section.** Every section, panel, list, menu and dialog that loads data shows its own loading state in place of
  its content. The page header, navigation, tabs and buttons that don't need the data stay usable.
- **Shaped like the content.** Lists: `<SkeletonList>`; card grids: `<SkeletonCards>`; forms: `<SkeletonForm>`; detail
  pages: `<SkeletonDetail>`; a single value such as a dashboard number: `<Skeleton>`; menus: `<MenuLoading>` as the
  first entry. Small or irregular areas use `<LoadingSection>`'s default spinner with its label ("Loading agents…").
- **No flash.** Placeholders stay invisible for the first 300 ms (the `.loading-placeholder` and `.skeleton` styles),
  so fast loads show nothing. Once visible, `<LoadingSection>` keeps the placeholder for at least 400 ms.
- **Use `<LoadingSection>`**: `<LoadingSection Loaded="Loaded" Error="@LoadError" Retry="RetryAsync" Label="Loading
  agents">` with a `<Placeholder>` and the real content as `<ChildContent>`. In a `LiveComponent`, `Loaded` only becomes
  true after a successful load and `LoadError` says why it failed. Components that load data themselves keep `_loaded`
  and `_loadError` the same way and catch `ApiException` and `HttpRequestException` (`LoadingSection.Unreachable`).
- **Empty, not found and errors only after loading.** Put them in the `<ChildContent>`. A failed load shows its error
  with Retry instead of an empty section.
- **Refreshing keeps the data.** Later reloads (live updates, polling) never show placeholders again; the data on screen
  stays until the new data replaces it.
- **Menus open at once** and show `<MenuLoading>` while their entries load; don't wait for data before opening.
- **Accessibility.** A placeholder has `role="status"`, `aria-busy="true"` and a visually hidden label; skeleton shapes
  are `aria-hidden`. One label per section, not per row.

`DeveAgentManager.Tests/Browser/loading-states.mts` checks this with a slow and a failing API.

## UI: hold to confirm

Disruptive actions that lose nothing for good, where a confirmation dialog would only slow people down, use
`<HoldButton>` (`DeveAgentManager.Client/Shared/HoldButton.razor`): press and hold it for 3 seconds. For now that is only
a Runner's Start, Stop, Restart and Update all (`RunnerPowerActions`, on the Runners page's cards and in the Runner
page's header, with the same explanations).

- **A click does nothing.** It shows "Hold 3 seconds to confirm" in a bubble on the button, which fades away once
  released, so it's clear nothing was confirmed.
- **Holding** fills the button and counts down 3, 2, 1 in the bubble. At 0 the bubble says what is happening
  (`ActiveText`, e.g. "Stopping…") and `OnConfirmed` runs.
- **Cancelling.** Releasing, moving off the button, scrolling (pointer cancel), Escape or the button becoming disabled
  before 0 cancels. Space and Enter can be held instead of a mouse button or finger.
- **Explain it.** `Help` says what the action does; it shows in a tooltip on hover and keyboard focus, with "Hold for 3
  seconds to confirm", and is read by screen readers.
- **Keep dialogs** for what can't be undone (deleting) and for actions that need a choice or input.

`DeveAgentManager.Tests/Browser/hold-to-confirm.mts` checks this with mouse, keyboard and touch.

## UI: agent order

Every list of agents puts the one that replied last first: sort with `AgentOrder.ByLastReply()`
(`DeveAgentManager.Client/Shared/AgentOrder.cs`), including lists cut short such as the dashboard's. Agents that never
replied come after, newest first. Lists grouped by agent, such as sessions, order the groups the same way.
`DeveAgentManager.Tests/Browser/agent-order.mts` checks the pages that list agents.

## Documentation privacy

Use reserved example domains (`example.com`, `example.org`, `.test`), placeholder credentials, and generic paths in
documentation and agent instructions. Do not copy an operator's hostnames, IP addresses, usernames, email addresses,
working folders, account IDs, passwords, API tokens, cookies, private keys or authentication files into tracked docs.
Loopback addresses and documented product defaults are allowed when needed to reproduce local tests. Public project
URLs and upstream references are allowed. Mark test-only credentials clearly; never substitute a live credential.
Before committing docs, review the diff and scan all tracked documentation for private deployment details and secrets.
Do not paste matching secret values into command output or reports. Removing a value from the current files does not
remove it from Git history; report any exposed credential so its owner can rotate it.

## Conversation synchronization

Keep periodic sync incremental and list metadata separate from full history reads. Preserve exact native records on
disk, including images and replay data. Periodic growing-file sync checks source identity and bounded head/tail samples;
older middle edits can remain undetected until a full audit. Audit after restart, when idle with pending appends, at
least every 30 minutes, and at lifecycle checkpoints. Verify pending history before export, copy or harness switch;
never treat a sampled checkpoint as fully verified. Same-size edits, truncation, replacement and interrupted sync
must preserve the last good checkpoint or rebuild safely. Test bounded reads, delayed-edit recovery, format,
metadata and active-branch parity plus real SSH transfer and copy/switch/export behavior. See
[docs/lean-session-sync.md](docs/lean-session-sync.md).

## Browser code

Prefer C# for application logic. Browser APIs, DOM measurements and adapters for JavaScript libraries belong in
`DeveAgentManager/Browser/*.ts`; browser tests use `DeveAgentManager.Tests/Browser/*.mts`, and Node unit fixtures use
`DeveAgentManager.Tests/Js/*.cts`. Do not edit generated JavaScript in `wwwroot/js` or `.typescript`, and do not rewrite
vendored libraries under `wwwroot/lib`.

`dotnet build` compiles TypeScript before static asset discovery (Node 22+ and npm required; dependencies are pinned
in the root package-lock.json). `npm ci --ignore-scripts` installs the toolchain, `npm run check` type-checks without
writing output, and `npm run build` emits browser `.js` and test/tool `.mjs`/`.cjs`. Run a browser test with
`node .typescript/DeveAgentManager.Tests/Browser/hold-to-confirm.mjs`. The Docker build installs Node only in its build
stage. Keep browser interop types explicit; migration currently allows implicit parameter types for older adapters
and dynamic test fixtures. Do not bypass compiler errors with `@ts-nocheck` or `noCheck`.

Timezone discovery stays in TypeScript; `BrowserClock.cs` owns conversion and invalid/ambiguous-time validation.
`AppUpdateService.cs` owns the update timer and reuses `PageVisibility`. Early theme and editor authentication scripts
are compiled TypeScript embedded in the Manager assembly, preserving their existing nonce and separate-origin flows.
