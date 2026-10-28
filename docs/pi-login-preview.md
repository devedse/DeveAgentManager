# pi saved logins and model cycling

pi remains one choice in the harness switch menu. Its cogwheel opens **My settings → Models**. A personal selection forwards multiple saved Claude Code and Codex subscriptions to pi at its next start; selecting or saving never starts, stops or switches a running agent.

## Configure and try it

1. Install pi on a test Runner. For Claude, also install Claude Code and the package `git:github.com/earendil-works/pi-claude-code`.
2. Connect accounts under **My settings → Harness logins**, or ask their owner for Use access. Reconnect an older Codex account if it needs Manager renewal.
3. Open the pi cogwheel, or **My settings → Models → pi → Runner logins**. Select accounts independently, including several Claude accounts and several Codex accounts. Save the page. A management role alone does not grant permission to forward a login.
4. Each selected account shows a stable provider identifier, `dam-claude-<account-id>` or `dam-codex-<account-id>`. Use **Add selected logins to model cycling** to add their model patterns without typing identifiers. An explicit default can use `provider/model`, such as `dam-claude-<account-id>/default` with the actual identifier.
5. Start a new pi test agent in an empty folder, or restart an existing pi agent explicitly. These are the agent owner's preferences, independent of its Git identity. A person switching a shared agent also needs Use access to every forwarded account.
6. Open `/model` to check that all selected accounts are available. `/scoped-models` shows the cycling list. A real prompt uses the chosen account's quota and billing rules; the automated compatibility check below sends no prompt.
7. Untick accounts and save to remove them at the next start. Logins configured through pi's own `/login` remain available. Removing a saved login's Use grant instead triggers enforcement against affected running processes.

## Connections and credentials

| Connection | How requests run | Credential handling |
| --- | --- | --- |
| Native pi logins | pi's configured providers | Existing pi authentication remains available alongside forwarded accounts. |
| Each saved Claude subscription | Official Claude Code executable through the bridge package | A separate wrapper and Claude configuration directory per account and pi agent. Every child reads that account's current Manager-maintained environment file. pi receives neither the token in its global environment nor refresh credentials. |
| Each saved Codex subscription | pi's native legacy Codex protocol | A separate provider reads that account's Manager-maintained access-token file for each request. No OAuth refresh or auth-file writer is installed in pi. |

The Claude bridge's built-in model catalog is used separately for each account; shared, ambient CLI model discovery is not used. Each stream supplies its own wrapper and configuration environment without changing the pi process environment. DAM never automatically substitutes another saved account when an explicit selection fails authorization. The Codex connection is distinct from pi's newer **OpenAI** sign-in, which uses a different OAuth grant.

Preferences are stored in the user's encrypted model settings and contain account IDs only. Launch validates all grants and distributes only authorized copies. Durable `AgentPiLogins` bindings record every account actually forwarded to a process, independently of later preference edits. Removing access, disabling or deleting any forwarded account stops the affected process through normal login enforcement. Bindings from an uncertain launch remain until a confirmed replacement or verified enforcement stop. SSH, uploads and launch work during a harness switch run outside the global session lock.

Older preview agents with a single explicit account keep that behavior until personal forwarding settings are saved. Their private pi runtime and native history remain in place. When the old account is also selected in the new list, the adapter maps its legacy provider to that same account; it never maps it to a different account. Update legacy cycling patterns to the new identifiers using the settings button.

## Model cycling settings

`/scoped-models`, `enabledModels` and `--models` express the same cycling scope. Provider prefixes avoid ambiguous model IDs. Patterns support globs and optional thinking suffixes, and their order controls the cycle. This does not restrict model access.

**Manager settings → pi** provides a shared list. **My settings → Models → pi** can inherit that list, explicitly select **All available models**, or save a personal ordered list. Null means inherit/preserve pi settings; an empty list means all available. DAM validates list size and pattern formatting, quotes CLI arguments, and passes the effective scope with `--models` at the next start. Normal default model changes still apply to new conversations. A scope change alone does not change a running model or restart an agent.

Saving `/scoped-models` inside pi changes pi settings. An explicit DAM scope takes precedence at the next start. Choose inheritance to leave the native settings in control.

## Verification

Unit/API tests cover multiple accounts, personal grants, incompatible and unavailable selections, revocation after preference changes, private history, separate Claude wrappers, token replacement without refresh or write, concurrent stream isolation, and scope inheritance. Browser tests cover the single pi menu, navigation without execution, loading and retry, four selected accounts, persistence, generated cycling patterns, and phone layouts in both themes.

With pi and the Claude bridge installed, run:

```sh
npm run build
node .typescript/tools/pi-login-smoke.mjs
```

This starts an isolated pi RPC process with dummy credentials, loads two Claude and two Codex providers, checks that their models are available and sends no prompt. It was checked against pi 1.4.0. Record real Runner deployment, credential renewal, provider responses and billing separately from host tests.

Upstream: [pi models](https://pi.dev/docs/latest/models), [pi settings](https://pi.dev/docs/latest/settings), [Claude Code bridge](https://github.com/earendil-works/pi-claude-code).
