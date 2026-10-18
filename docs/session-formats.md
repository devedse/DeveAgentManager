# Session formats

DeveAgentManager exposes every conversation in one harness-neutral format, the **DeveSession**, so a session can be
viewed in the Portal, survive its Runner, be resumed on another Runner and later be continued in another harness.
This document describes the native formats of pi, Claude Code and Codex, the DeveSession format, and how they map.

Status: pi, Claude Code and Codex support reading and writing. Native histories are synchronized incrementally; see
[lean conversation synchronization](lean-session-sync.md) for storage, recovery and regression checks.

## Native formats

All three store a session as a JSONL file (one JSON object per line) that only grows at the end.

### pi

`~/.pi/agent/sessions/--<cwd>--/<timestamp>_<session-id>.jsonl`, where `<cwd>` is the working directory with the
leading `/` removed and `/`, `\` and `:` replaced by `-` (for example `--home-runner-work-example--`). pi finds a session
by the `id` in its header, **only in the folder of the current working directory**. The file name doesn't matter.

| Line `type` | What it is |
|---|---|
| `session` | First line: `version` (3), `id`, `timestamp`, `cwd`, optional `parentSession`. |
| `message` | An `AgentMessage` with `role`: `system` (prompt sections and tool declarations), `user`, `assistant`, `toolResult`, `bashExecution` (`!command` typed by the user), `custom` (extension message), `branchSummary`, `compactionSummary`. |
| `compaction` | Summary of older messages; `firstKeptEntryId` says where the kept part starts. |
| `branch_summary` | Summary of an abandoned branch (`/tree`). |
| `model_change`, `thinking_level_change` | Settings changed mid-session. |
| `session_info` | Session name (`/name`, `--name`). |
| `label`, `context_edit`, `usage`, `custom`, `custom_message` | Bookmarks, context edits, extra usage, extension data and extension messages. |

Every entry except the header has `id` (8 hex chars), `parentId` and an ISO `timestamp`. Entries form a **tree**:
branching (`/tree`, `/fork`) adds children to an earlier entry, and the conversation is the path from the last entry
back to the root.

Content blocks: `text`, `image` (`data` base64 + `mimeType`), `thinking` (`thinking`, opaque `thinkingSignature`,
`redacted`) and `toolCall` (`id`, `name`, `arguments` object). Tool results are separate `toolResult` messages with
`toolCallId`, `toolName`, `content` and `isError`. Assistant messages carry `provider`, `model`, `usage` (tokens and
cost) and `stopReason`.

### Claude Code

`~/.claude/projects/<cwd with every non-alphanumeric character replaced by '-'>/<session-uuid>.jsonl`.

| Line `type` | What it is |
|---|---|
| `user` | `message` in Anthropic API form: `content` is a string or blocks. **Tool results are `tool_result` blocks inside user messages**, with extra `toolUseResult` metadata on the line. |
| `assistant` | Anthropic API response: blocks `text`, `thinking` (with `signature`), `tool_use` (`id`, `name`, `input`). One API response can be split over several lines (same `message.id`). |
| `system` | Hook runs, notices, compaction boundaries. |
| `summary`, `ai-title`, `last-prompt`, `mode`, `permission-mode`, `file-history-snapshot`, `file-history-delta`, `queue-operation`, `attachment` | UI and bookkeeping state, not conversation. |

Lines are linked with `uuid` / `parentUuid` (a tree, like pi) and have `sessionId`, `cwd`, `gitBranch`, `version`
and `isSidechain` (subagent). `claude --resume <uuid>` resumes it.

### Codex

`~/.codex/sessions/YYYY/MM/DD/rollout-<timestamp>-<uuid>.jsonl`. Every line is `{timestamp, type, payload}`:

| `type` / `payload.type` | What it is |
|---|---|
| `session_meta` | First line: `id`, `cwd`, `cli_version`, `model_provider`, base instructions. |
| `turn_context` | Per-turn settings: model, approval and sandbox policy. |
| `response_item` / `message` | OpenAI Responses API message; `role` `user`, `assistant` or `developer`; blocks `input_text`, `output_text`, `input_image`. |
| `response_item` / `reasoning` | Reasoning: `summary` plus **`encrypted_content`** that only OpenAI can read. |
| `response_item` / `function_call`, `custom_tool_call` | Tool calls (`call_id`, `name`, JSON `arguments` or free-form `input`). Current Codex does nearly everything through one `exec` tool that runs a script. |
| `response_item` / `*_output` | Tool results, matched by `call_id`. |
| `event_msg` / `token_count`, `task_started`, ... | UI events and usage. |

The history is **linear** (no tree). `codex resume <uuid>` resumes it.

## DeveSession

The portable/imported format is one JSON document per session, stored in `/data/sessions/<session-id>.json`.
Synced native histories use the native store described above and produce this same structure when loaded or exported:

```jsonc
{
  "format": "deve-session",
  "version": 1,
  "title": "Fix maze generator performance",
  "cwd": "/home/runner/work/example-project",
  "createdAt": "2026-09-25T11:07:36Z",
  "updatedAt": "2026-09-25T12:40:02Z",
  "leafId": "c3d4e5f6",                  // where the conversation currently is (the tree's active end)
  "sources": [                            // which harness sessions this was built from
    { "harness": "pi", "sessionId": "01a0...", "header": { /* native header line */ } }
  ],
  "entries": [
    {
      "id": "a1b2c3d4", "parentId": null, "timestamp": "2026-09-25T11:07:40Z",
      "kind": "message",                  // message | summary | event | native
      "role": "assistant",                // system | user | assistant | tool
      "content": [
        { "type": "thinking", "text": "...", "signature": { "harness": "pi", "provider": "anthropic", "value": "..." } },
        { "type": "text", "text": "Let me look at the tests." },
        { "type": "toolCall", "id": "toolu_01", "name": "bash", "toolKind": "shell", "arguments": { "command": "dotnet test" } }
      ],
      "model": { "provider": "anthropic", "id": "claude-opus-4-5" },
      "usage": { "input": 1200, "output": 300, "cacheRead": 50000, "cacheWrite": 0, "reasoning": 120, "costUsd": 0.021 },
      "stopReason": "toolUse",
      "harness": "pi",
      "native": { /* the original pi line, unchanged */ }
    },
    {
      "id": "b2c3d4e5", "parentId": "a1b2c3d4", "kind": "message", "role": "tool",
      "content": [ { "type": "toolResult", "toolCallId": "toolu_01", "toolName": "bash", "isError": false,
                     "content": [ { "type": "text", "text": "Passed! 18 tests" } ] } ]
    },
    { "id": "...", "kind": "summary", "summaryKind": "compaction", "text": "Earlier: ...", "firstKeptId": "..." },
    { "id": "...", "kind": "event", "event": "model_change", "data": { "provider": "openai", "model": "gpt-5" } },
    { "id": "...", "kind": "native", "harness": "pi", "native": { /* e.g. an extension's custom entry */ } }
  ]
}
```

Content blocks: `text`, `image` (`mimeType`, base64 `data`), `thinking` (`text`, `redacted`, opaque `signature`),
`toolCall` (`id`, `name`, `toolKind`, `arguments`) and `toolResult` (`toolCallId`, `toolName`, `isError`, `content`).

### Rules

1. **Lossless for the same harness.** Every entry keeps its original line in `native`. Writing a DeveSession back
   to the harness it came from emits those lines unchanged. Only the header is rewritten (new id and working
   directory when a session moves to another Runner or folder).
2. **Neutral fields are a view.** `role`, `content`, `model`, `usage` and friends are derived from `native`. They're
   what the Portal shows and what a converter for *another* harness uses.
3. **A tree, not a list.** `id` / `parentId` keep pi's and Claude Code's branches. `leafId` marks the active branch.
   Codex (linear) becomes a single chain.
4. **Unknown lines are kept, not dropped.** Lines without a neutral meaning (UI state, extension data) become
   `kind: "native"` entries. They go back into their own harness and are skipped for others.

## Moving between harnesses

**Continue with…**, importing/continuing a conversation in a differently configured agent, and exports to another harness use
`SessionMigration`. Native records from the same harness keep their existing lossless path. Periodic synchronization
still copies native bytes incrementally; it never runs conversion or loads full histories to populate lists.

The converter reads the active branch and groups assistant calls with their results. Complete exchanges keep their
original tool names, JSON arguments, output, errors and images. Tool IDs are remapped consistently to portable IDs;
parallel calls stay together. Claude writes `tool_use` and `tool_result` blocks, Codex writes function/custom-call
response items and matching outputs, and pi writes tool calls and result messages. Codex terminal display events
are separate from model history and never count as another execution.

Completed foreign tools need not exist in the destination tool set: the supported CLIs preserve them as history.
Names are not translated into superficially similar tools. In particular, a Codex code-mode script remains one
opaque call and result. The converter does not evaluate JavaScript or infer which nested calls executed. It never
runs a historical command, retries a pending job or treats a started background process as completed. New work uses
the destination harness's actual tools, workspace, account and selected model.

Incomplete exchanges, duplicate IDs/results, malformed arguments, unsupported names and oversized exchanges become
labelled historical records. These are data, not new user instructions or completed tool exchanges. Source shell
execution events and compaction summaries are also labelled as historical context. Ordinary assistant text claiming
that a command ran remains assistant text; it is never promoted into evidence of execution. Archived context is
shown separately in the conversation viewer. Missing results are not replaced with invented success/error outputs.

The default context allowance is 320,000 estimated characters, reserving 1,500 for a transfer notice. This is a
conservative text/image budget, not an exact token count or a promise about every configurable model's context
window. Complete exchanges are selected newest first, while preserving the latest actual user request. Older units
that do not fit are omitted explicitly. A single exchange larger than half the allowance becomes a bounded archival
excerpt. The latest user request is never silently truncated; if it cannot fit, conversion fails with the original
session still available. Up to 20 recent images share a 12,000,000-character base64 allowance, with a per-image
limit of 5,000,000 base64 characters; images in tool results count too. Omitted images leave a labelled placeholder.
Arguments of a retained structured call are never shortened. All original records stay in the source backup.

Source system prompts, hidden reasoning and provider replay signatures are excluded from foreign model context.
They remain in the original native archive. The new harness supplies its own system instructions. Only the active
branch moves; abandoned branches remain available in the source. A second move replaces the previous transfer
notice. Envelope metadata (`damHistoryKind`, `damToolHistory`) preserves archival provenance and custom-call
encoding through repeated switches without adding fields to provider message bodies or changing tool arguments.

Before installation, the destination bytes are read back and checked for paired calls, exact arguments/results,
error state, encoding hints and user images. A failed writer aborts before upload. The Runner receives a private
staging file, verifies its SHA-256 and atomically renames it. A cancelled or failed transfer cannot overwrite the
last good file. An uncertain SSH acknowledgement is never permission to replay an installation or user work.
The stored session mapping changes only after a successful installation. Failed startup leaves the selected
conversation in an error state available for an explicit Start; it does not automatically rerun the switch.

The original conversation is backed up as `/data/sessions/<id>.<time>.<harness>.json`. `HarnessTrail` records the
route. Content-free conversion reports live in `/data/sessions/migrations/<id>.json`, bound to the destination's
native session ID; they survive subsequent native sync and are copied with a continued session. They contain
adapter version, time, route, budget and counts/reasons, never commands or output. The session page shows the
latest report behind a help tip. Deleting a session also deletes its report and conversion backups.

### Verification and compatibility

`SessionMigrationTests` covers all six directions between Claude Code, Codex and pi, repeated moves, parallel calls,
errors, tool images, opaque code-mode scripts, malformed/incomplete calls, literal fabricated command narratives,
branches, budget limits and source immutability. `NativeHistoryTransportTests` covers real SSH installation,
checksum/cancellation rejection, writer loss, source/mapping recovery, exports, copies, reports after sync and deletion.
Existing format and incremental-sync checks continue to cover native preservation.

Run `python3 tools/session-migration-gate/gate.py` with .NET 10 and the three CLIs installed. It generates sessions
using the real DAM readers/writers, resumes them with isolated temporary configuration and fake credentials, and
serves model responses from a loopback stub. Each route must send structured historical calls/results, execute a
fresh real shell tool, persist its native call record and leave historical side effects untouched. Pi is checked
with both Anthropic and OpenAI wire formats. The gate uses no subscription or real model API. Verified locally with
Claude Code 2.1.302, Codex 0.162.1 and pi 1.2.0. Re-run it after harness upgrades. Other pi providers may transform
history differently; the gate does not establish every hosted provider's acceptance policy or a model's behaviour.

Existing flattened histories are not automatically repaired: prose cannot establish which commands executed.
Preserve the current conversation, identify the last trustworthy native backup, and create an explicit continuation
using verified workspace state and subsequent user instructions. Keep the affected original for inspection. This
change does not restart or rewrite existing agents, and does not guarantee that a model will never fabricate claims.

## Workspaces and selected conversations

Each agent is a workspace (repository, Runner, and working folder). It can own multiple stored sessions,
with exactly one selected at a time. `WorkspaceAgentId` retains that ownership; nullable `AgentId` selects
which conversation runs. A unique database index enforces one selected conversation per agent. Existing
sessions are assigned to their current workspace during migration. This leaves workspace identity separate
from repository URL, so future worktrees can have distinct working folders without merging their chats.

The folder button in the terminal opens the session picker. It creates, selects, renames, and deletes saved
sessions. The Sessions page groups them by workspace, with the selected conversation first. Deleting requires
confirmation and is allowed only for inactive sessions. It removes Manager history and conversion backups;
Runner files and repository files are untouched. Deleting a workspace leaves its sessions available for export
or continuation on a new Runner.

Selection stops the current harness gracefully and performs a final sync before changing the selected chat.
It restores each saved chat in its own recorded harness, using a fresh native session ID. The saved harness must
be installed; when it differs, the owner's permitted default login is checked before stopping the current process.
An incompatible login is cleared, and the harness, login selection, active session and native ID change together.
The saved harness's model is resumed normally. New chats use the workspace's current harness. Selecting a chat does
not convert its history or append a harness to its trail; use **Continue with…** for an explicit conversion.
Other chats remain saved. A failed final sync or
missing saved history aborts selection; a failed launch leaves the selected chat available to retry with Start.
Lifecycle changes and background observation/sync are serialized so an old snapshot cannot change the new
chat's state. Continuation schedules are bound to the old native session identity and cannot run in a new chat.

Automatic titles come from the existing format readers (Claude's custom title/summary, Pi's session name,
or the first prompt fallback, including Codex). A Manager rename pins the display name against subsequent
syncs. Harness conversion also preserves the name and takes a backup before converting history. This does
not rename the repository or guarantee that the harness's own resume menu adopts the Manager's display name.

The picker shows the latest synced native harness file size, and the total Manager history plus conversion
backups. These are file bytes, **not** model context-window capacity or remaining tokens. Context usage is not
inferred from file size. Project files are shared across sessions; switching does not create a worktree.

When transferring a conversation into Claude, the launch explicitly passes the Manager's Claude default
model with `--model` (or `default` if none is configured). Claude's resume behavior can restore a model
from the transcript ahead of `settings.json`; model fields inserted for compatibility during conversion
must not choose the new session's model. This applies to explicit harness switches
and continuing an imported conversation on a new agent. Ordinary restarts and saved-session selection keep
the conversation's model choice. See https://code.claude.com/docs/en/model-config.
