# pi saved-login preview and model cycling

This preview connects one saved Claude or Codex subscription login to a pi agent. It is opt-in per agent. Existing pi `/login` credentials remain a separate choice.

## Test in a separate workspace

1. Install pi on a test Runner. For Claude, install Claude Code too and apply the pi package configuration containing `git:github.com/earendil-works/pi-claude-code`.
2. Connect the subscription under **My settings → Harness logins**. Reconnect an older Codex login if DAM says it needs Manager renewal. Both the person selecting the login and the agent owner need Use access.
3. Under **My settings → Models → pi → Model cycling**, choose **Choose model patterns**. Enter an ordered list such as `claude-code/default:high` for Claude or `dam-codex/<model-id>:high` for a Codex model available in the installed pi catalog. Use the actual model ID instead of the placeholder.
4. Create a new pi agent in an empty folder, for example `~/work/pi-login-preview`, using **pi login preview → Login source** to select the saved subscription. This keeps the test conversation independent from other agents. If testing in a project workspace instead, open that workspace's harness menu and choose **pi → Saved login (preview)**.
5. Confirm that the footer uses `claude-code` or `dam-codex` before sending a small test prompt. A real prompt uses the selected subscription's normal quota and billing rules. `/claude-code-status` checks the Claude CLI connection; `/scoped-models` displays the cycling list.
6. For an existing pi agent, its harness menu opens the same selector. Choosing a login in the dialog does nothing until the separate restart confirmation is accepted. The restart resumes that workspace's conversation.
7. Choose **Runner logins (/login)** to return to pi's own authentication. A private runtime already created for this agent remains the history location, preserving the conversation.

## What the choices mean

| Choice | Provider | Credential handling |
| --- | --- | --- |
| Runner logins | pi's configured providers | Existing pi authentication; no saved DAM account is selected automatically. |
| Saved Claude subscription | `claude-code` | DAM supplies the selected subscription token to the official CLI bridge. Its Claude configuration/history directory is private to this pi agent. |
| Saved Codex subscription | `dam-codex` | DAM's adapter uses pi's native legacy Codex protocol and reads the Manager-maintained access-token file for each request. No refresh token or OAuth writer is installed in pi. |

The Codex preview is distinct from pi's newer **OpenAI** sign-in, which requests a different OAuth grant. Selecting a saved Codex login does not convert that grant or authenticate the newer provider. The existing account renewal, sharing, usage-impact and revocation paths track the pi binding through its selected/running account. Revocation stops the affected pi process using the same durable enforcement as other harnesses.

At startup the preview preserves a resumed model already on the connected provider. Otherwise it chooses a model on that provider, preferring its cycling scope. An explicit default model should include the managed provider prefix, such as `claude-code/default` or `dam-codex/<model-id>`. Defaults for another provider do not override the selected login. Afterwards `/model` can still change providers; the selected DAM login is a credential binding, not a model-access restriction.

## Model cycling settings

`/scoped-models`, `enabledModels` and `--models` express the same cycling scope. Provider prefixes avoid ambiguous model IDs. Patterns support globs and optional thinking suffixes, and their order controls the cycle. This does not restrict model access.

**Manager settings → pi** provides a shared list. **My settings → Models → pi** can inherit that list, explicitly select **All available models**, or save a personal ordered list. Null means inherit/preserve pi settings; an empty list means all available. DAM validates list size and pattern formatting, quotes CLI arguments, and passes the effective scope with `--models` at the next start. Normal default model changes still apply to new conversations. A scope change alone does not change a running model or restart an agent.

Saving `/scoped-models` inside pi changes pi settings. An explicit DAM scope takes precedence at the next start. Choose inheritance to leave the native settings in control.

## Verification

The unit/API tests cover grant checks, incompatible account rejection, revocation, native cache sharing, private history, token replacement without refresh/write, model selection and scope inheritance. `DeveAgentManager.Tests/Browser/pi-setup.mts` covers real settings persistence, validation, light/dark phone layouts, explicit login selection and cancellation; Runner metadata and control requests are mocked there.

With pi installed, run this compatibility check from the repository root:

```sh
npm run build
node .typescript/tools/pi-login-smoke.mjs
```

It starts an isolated pi RPC process with a dummy token, loads the actual adapter, checks its provider/model scope and sends no model prompt. The preview was checked against pi 1.4.0. Record a real Runner deployment, credential renewal and provider response separately from these host tests; a passing smoke check does not verify a live account or billing.

Upstream: [pi models](https://pi.dev/docs/latest/models), [pi settings](https://pi.dev/docs/latest/settings), [Claude Code bridge](https://github.com/earendil-works/pi-claude-code), [OpenAI authentication](https://learn.chatgpt.com/docs/auth).
