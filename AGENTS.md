# Notes for agents working on DeveAgentManager

## Background verification

Use the supported owned-job helper and completion delivery described in
[docs/background-jobs.md](docs/background-jobs.md) for long verification work.
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

## Documentation privacy

Use reserved example domains (`example.com`, `example.org`, `.test`), placeholder credentials, and generic paths in
documentation and agent instructions. Do not copy an operator's hostnames, IP addresses, usernames, email addresses,
working folders, account IDs, passwords, API tokens, cookies, private keys or authentication files into tracked docs.
Loopback addresses and documented product defaults are allowed when needed to reproduce local tests. Public project
URLs and upstream references are allowed. Mark test-only credentials clearly; never substitute a live credential.
Before committing docs, review the diff and scan all tracked documentation for private deployment details and secrets.
Do not paste matching secret values into command output or reports. Removing a value from the current files does not
remove it from Git history; report any exposed credential so its owner can rotate it.
