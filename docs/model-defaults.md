# Default models and effort

**Manager settings → pi / Claude Code / Codex / Grok** set the default model and effort for everyone. Empty fields
let the harness choose. **My settings → Models** lets each user choose their own for each harness. A field left empty
there follows the Manager's default, also when it changes later. **Use Manager default** clears both fields. An agent
uses its owner's choice, whoever starts it.

The Manager writes its defaults to each harness's own settings file on the Runners, which every agent there shares.
A user's choice can't go into a shared file, so the Manager passes the defaults as launch flags. Harnesses keep some
settings in the conversation itself. Those settings win when a conversation resumes, so `/model` changes inside a
conversation survive restarts. Each harness therefore gets the defaults as follows:

| Harness | Model | Effort |
|---|---|---|
| Codex | Until the conversation has a Codex turn, or after a switch from another harness (`-m`) | Same as the model (`-c model_reasoning_effort=…`) |
| Claude Code | When the conversation starts, or after a switch from another harness (`--model`) | At every start (`--effort`) |
| pi | When the conversation starts, or after a switch from another harness (`--provider`, `--model`) | Same as the model (`--thinking`) |
| Grok | When the conversation starts, or after a switch from another harness (`--model`) | At every start (`--reasoning-effort`) |

The reasons, verified with Codex 0.163 and Claude Code 2.1.310:

- **Codex** resumes with the model and effort of the conversation's last turn (`turn_context`), not `config.toml`'s.
  The Manager starts even a new conversation with `codex resume`, from a session file holding only its header. Without
  a recorded turn, Codex used its own default effort, not the configured one. A conversation converted from another
  harness has no Codex turn either, or an old one from before. So the defaults are given until Codex records a turn,
  and always right after a switch. An account switch keeps the conversation's own settings.
- **Claude Code** restores a resumed conversation's model from the transcript. A converted transcript carries a
  compatibility model, so a switch gives the default model, or `default` (the login's own) when there is none.
  Claude doesn't restore the effort on resume. It reads a top-level `effortLevel` in the user settings file only for
  older models, so the effort is given at every start.
- **pi** records model and thinking level changes in the conversation and resumes with them.
- **Grok** records the conversation's model (`current_model_id`) but not its effort.

Flags that older CLI versions don't have (Claude's `--effort`, Grok's `--reasoning-effort`) are passed only when the
installed version's `--help` lists them. Otherwise the CLI would refuse to start.

The user's choices are stored encrypted per user (`UserSetting` key `models`). `GET /api/me/settings/models` returns,
per harness, the Manager's default model fields and the user's own. It returns no other part of the shared Setup
Profile. `PUT` takes `{ "<harness>": { "model": …, "effort": … } }`. Harnesses left out, and empty fields, follow the
Manager's default.
