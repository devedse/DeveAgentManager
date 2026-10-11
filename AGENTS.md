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

`DeveAgentManager.Tests/Browser/loading-states.mjs` checks this with a slow and a failing API.

## UI: agent order

Every list of agents puts the one that replied last first: sort with `AgentOrder.ByLastReply()`
(`DeveAgentManager.Client/Shared/AgentOrder.cs`), including lists cut short such as the dashboard's. Agents that never
replied come after, newest first. Lists grouped by agent, such as sessions, order the groups the same way.
`DeveAgentManager.Tests/Browser/agent-order.mjs` checks the pages that list agents.

## Documentation privacy

Use reserved example domains (`example.com`, `example.org`, `.test`), placeholder credentials, and generic paths in
documentation and agent instructions. Do not copy an operator's hostnames, IP addresses, usernames, email addresses,
working folders, account IDs, passwords, API tokens, cookies, private keys or authentication files into tracked docs.
Loopback addresses and documented product defaults are allowed when needed to reproduce local tests. Public project
URLs and upstream references are allowed. Mark test-only credentials clearly; never substitute a live credential.
Before committing docs, review the diff and scan all tracked documentation for private deployment details and secrets.
Do not paste matching secret values into command output or reports. Removing a value from the current files does not
remove it from Git history; report any exposed credential so its owner can rotate it.
