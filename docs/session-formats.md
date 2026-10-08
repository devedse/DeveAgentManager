# Session formats

DeveAgentManager stores every conversation in one harness-neutral format, the **DeveSession**, so a session can be
viewed in the Portal, survive its Runner, be resumed on another Runner and later be continued in another harness.
This document describes the native formats of pi, Claude Code and Codex, the DeveSession format, and how they map.

Status: **pi is implemented** (read and write). Claude Code and Codex are researched below but not implemented.

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

One JSON document per session, stored by the Manager in `/data/sessions/<session-id>.json`:

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

**Continue with…** in an agent's terminal, or continuing a stored session with another harness, uses
**conversation as text** (`SessionFlattener`):

- The active branch becomes alternating user and assistant messages with **text and images only**. Tool calls become
  short lines like ▸ ran `dotnet test` or ▸ edited `src/Maze.cs`, with their output trimmed to about 1200 characters.
  Summaries become a user message ("Summary of the earlier conversation"), and pi's `!` shell runs are included.
  Codex's code-mode `exec` scripts are read as the tools they call (each `exec_command`, `apply_patch`, `view_image`
  and web search becomes its own line), and their JSON results are unwrapped to the command output, with
  `(exit code N)` or `(still running)` where that applies.
- **Thinking stays behind**: signatures (Anthropic) and encrypted reasoning (OpenAI) only work for the provider that
  made them.
- The first message says where the conversation came from. Conversations longer than about 400k characters keep
  their most recent part, with a note saying how many messages were left out. An image counts as 6,000 characters
  (about what a model charges for it, whatever its file size), and only the 20 most recent images are kept.
- The target harness gets it as its own session file under a new id, which it resumes: `claude --resume`,
  `codex resume` (the Manager writes Codex's `session_meta` header itself), and pi's `--session-id`. The stored
  conversation before the move is kept next to it in `/data/sessions` (`<id>.<time>.<harness>.json`).
  `HarnessTrail` records the way it went, e.g. `pi,codex,claude`.

Why text and not translated tool calls: providers reject tool calls and results that don't pair up exactly, and a
model can't be handed calls to tools it doesn't have. Text is what every harness resumes reliably.

Reference for a later, fuller translation. What carries over, and what can't:

| | pi | Claude Code | Codex |
|---|---|---|---|
| Text, images | yes | yes | yes |
| Tool calls and results | `toolCall` + `toolResult` message | `tool_use` + `tool_result` block in a user message | `function_call` / `custom_tool_call` + `*_output` |
| Thinking | text + signature (provider-specific) | text + signature (Anthropic only) | `encrypted_content` (OpenAI only) |
| Branches | tree | tree | linear |
| System prompt | stored (`system` messages) | not stored | stored (`session_meta`, `developer`) |

- **Thinking signatures only work for the provider that made them.** When the target harness talks to another
  provider, thinking is sent as plain text (or dropped when there is none, as with Codex's encrypted reasoning).
- **Tool calls map by kind.** Every tool call gets a `toolKind`, and the target harness's equivalent is used where one
  exists. Other tools (MCP, extensions, Codex's `exec` scripts) become text in the conversation ("ran `x`, output:
  ..."), because a model can't be handed calls to tools it doesn't have.

  | `toolKind` | pi | Claude Code | Codex |
  |---|---|---|---|
  | `shell` | `bash` (`command`) | `Bash` (`command`) | `exec` / `exec_command` |
  | `read` | `read` (`path`, `offset`, `limit`) | `Read` (`file_path`, `offset`, `limit`) | via `exec` |
  | `write` | `write` (`path`, `content`) | `Write` (`file_path`, `content`) | `apply_patch` |
  | `edit` | `edit` (`path`, `edits`) | `Edit` (`file_path`, `old_string`, `new_string`) | `apply_patch` |
  | `search` | (extensions) | `Grep`, `Glob` | via `exec` |
  | `web` | `web_search`, `fetch_content` (extensions) | `WebSearch`, `WebFetch` | `web_search` |

- **System prompts are not carried over.** Every harness brings its own; only the conversation moves.
- **Only the active branch moves** to a harness without trees. The full tree stays in the DeveSession.

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
It then restores the chosen chat into the workspace's current harness, using a fresh native session ID. The
selection and agent's native ID are committed together. Other chats remain saved. A failed final sync or
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
must not choose the new session's model. This applies to harness switches, cross-harness session selection,
and continuing an imported conversation on a new agent. Ordinary restarts and same-harness resumes keep
the conversation's model choice. See https://code.claude.com/docs/en/model-config.
