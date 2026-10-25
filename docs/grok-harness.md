# Grok Build

Select **Grok** when creating a Runner, or install it from an existing Runner's page. The Manager installs xAI's
official [`@xai-official/grok`](https://docs.x.ai/build/overview) CLI. Connect an account under **My settings → Harness
logins → Grok** using device authentication or an xAI API key. A Grok account's access depends on its plan.

**Manager settings → Grok** selects the default model and reasoning effort. Empty fields let the CLI choose. Users can
choose their own under **My settings → Models**; the model is given when a conversation starts and the effort at every
start (see [default models](model-defaults.md)).
The Manager writes an overlay to `~/.deveagentmanager/grok-config.json`; native `~/.grok/config.toml` settings remain
available. Save and apply to update Runners; restart an agent to load the new launch settings. Models come from the
installed CLI's catalog. Instructions passed at launch use `--rules`, which adds them to Grok's system prompt.

## Accounts and runtime

Each agent has a private `GROK_HOME`. Shared configuration and skills are linked into it, and only the authorized
conversation is bootstrapped from the import location. Its login is selected through `GROK_AUTH_PATH`, independently
of the conversation. A short, separate Unix socket path avoids the platform limit on socket names.

The Manager retains the rotating refresh token, renews the official `auth.x.ai` device login centrally, and sends
access tokens to Runners. API-key accounts clear inherited Grok credentials before setting `XAI_API_KEY`.
Subscription accounts clear inherited API keys. Reconnecting an account uses the existing account access controls.

Terminal status recognition uses Grok's default controls. Grok usage-limit reset messages are not parsed yet, so
automatic usage-limit continuation is unavailable for this harness.

## Native histories

Grok writes sessions under `GROK_HOME/sessions/<encoded-working-directory>/<session-uuid>/`. Normal working directories
use URL encoding; long names use Grok's slug and BLAKE3 naming rule with a `.cwd` marker.

`updates.jsonl` contains timestamped `session/update` ACP records and `_x.ai/session/update` extensions. The adapter
joins streamed text for display, retains tool calls and completed results, and follows rewind markers when selecting
the active branch. Every native record remains in the saved raw log. The metadata index omits stream-only events and
keeps bounded message snippets. Images and unknown extension payloads remain in the raw records.

Grok also writes `summary.json`, `system_prompt.txt`, `prompt_context.json`, `tool_definitions.json`,
`chat_history.jsonl` and compaction checkpoint files. The Manager saves the small files when their metadata changes,
downloads each immutable checkpoint once, and synchronizes `chat_history.jsonl` incrementally using the same prefix
verification policy as the event log. It does not build a second message index for that stream.

Keeping the model history is necessary with CLI 1.0.62: its cold rebuild truncates history at a compaction marker
without restoring the checkpoint's base. The checkpoint and model history are both required for a compacted backup.
See the upstream [rebuild implementation](https://github.com/xai-org/grok-build/blob/2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8/crates/codegen/xai-grok-shell/src/session/storage/mod.rs).

Companion generations are committed with the event-log checkpoint. An interrupted download leaves the previous
generation readable. A changed log during companion capture prevents publication. Old manifests and stream
generations are pruned after a successful commit. Pending sampled data must pass full verification before export,
copy or harness switching. Periodic synchronization never materializes the full conversation.

## Export and restore

**Grok session** exports a `.grok.json` bundle containing the native event stream and replay files. Import it through
the Sessions page; the Manager restores the native folder before starting `grok --resume <uuid>`. A DeveSession
export also preserves the replay files in the Grok source header. Bare `updates.jsonl` imports work for conversations
that do not require missing compaction state; incomplete compacted imports cannot be continued.

Copying assigns a new session UUID and working directory. Native model history and checkpoint records remain intact.
Switching harnesses uses the existing bounded migration flow, including completed tool exchanges and images.

## Verification

The .NET tests cover incremental metadata parity, rewrites and truncation, interrupted commits, rewind branches,
account isolation, central renewal, export bundles and real SSH companion transfer. Captured CLI terminal screens
cover ready and working states without matching quoted activity text.

For an installed official CLI, set `DAM_GROK_CLI` to its executable and run the `GrokCliTests` test. It uses a local
mock model and disposable homes to verify native tool replay, compaction, bundle export and resume in a fresh home.
Set `DAM_GROK_TUI=1` to exercise the interactive terminal too. No live login or paid inference request is needed.
