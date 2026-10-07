# Notes for agents working on DeveAgentManager

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
