# Agent task lists

An agent can show the user its progress as a short task list: each task's name, % done, a description (what the task
is, or what was done), the time it took and an estimate of the time left. The agent keeps the list with
`dam-tools tasks` on its Runner; the Manager shows it next to the agent's terminal.

## In the Portal

The terminal toolbar has a **Tasks** button next to Shared images and Shared files. Once the agent has a list, a ring
around the button shows the overall progress (every task's % counts the same) and turns green when everything is done.
A dot in the corner means the list changed since you last opened it in this browser; it goes when you close the
dropdown. Its tooltip and screen-reader label say how many tasks are done.

The button opens a dropdown with the overall bar, "X of N done · time taken · time left", and every task with its
state (done, in progress, not started), %, description and times. Time left counts down from when the agent gave its
estimate. On narrow screens the button moves into the ⋮ menu, which opens the same list in a dialog.

People with read-write access to the agent can **Clear** the list, after an inline confirmation. That removes every
task for everyone who can see the agent, and the agent reads an empty list from then on. It can't be undone. Clearing
needs the agent's Runner to be ready, because the list lives there. Read-only viewers see the list without Clear.

## For agents

```sh
dam-tools tasks                                  # print the list (also: tasks show, tasks --json)
dam-tools tasks set "Read the code" "Write the fix" "Test it"
dam-tools tasks update 2 --percent 40 --left 15m --description "Streams to a temporary file"
dam-tools tasks done 1 --description "Found the cause in the export"
dam-tools tasks add "Update the docs"
dam-tools tasks apply --json '[{"id": 3, "percent": 50, "left": "5m"}, {"name": "Release notes"}]'
dam-tools tasks set --json '[{"name": "Build", "percent": 40, "description": "...", "left": "10m"}]'
dam-tools tasks remove 4
dam-tools tasks clear
```

`apply` changes several tasks in one go: an item with an `id` updates only the fields it gives, and one without an
`id` is added. `set` replaces the whole list, from names or a JSON array; `--json -` reads the array from standard
input. Every command prints the resulting list. Durations are written like `90s`, `5m`, `1h30m` or `2h`; a plain
number is minutes.

The time taken is measured unless the agent gives `--taken`. A task starts with its first progress, or, if it is only
reported once done, when it was added or the previous task finished, whichever is later. Reaching 100% records when
it finished and drops the time-left estimate.

The Manager adds a sentence about this to the system prompt of each harness it starts, as for sharing images and files
([Runner desktop](runner-desktop.md#how-agents-know-about-it)): set the list at the start of multi-step work, update a
task each time one is finished, with what was done, and keep % and time-left estimates current. `dam-tools tasks`
outside an agent (without `DAM_AGENT_ID`) refuses with an explanation.

Limits: 50 tasks, 120 characters per name and 1000 per description (line breaks kept, other control characters
dropped), and a 1 MiB list file.

## Storage and collection

The list is `~/.deveagentmanager/tasks/<agent-id>.json` on the Runner, with a revision that every change increases.
Changes take a per-agent `flock`, so concurrent updates never lose each other, and are installed by atomic rename.

The Manager reads lists in the SSH command that already checks the Runner's agents every 5 seconds: it passes the
revision it has for each agent (`--changed-tasks <agent>=<revision> …`), and only lists with another revision come back,
as one marked base64 JSON line each. Unchanged lists cost no transfer and no extra SSH round trip. The Manager checks
and bounds every field again (text and numbers only), applies lists only to that Runner's own agents, keeps the last
copy in its database (`Agent.TasksJson`, `Agent.TasksRevision`) and announces a change to the Portal (`TasksChanged`),
which then reloads that agent's list. A stopped or unreachable Runner keeps showing the last list read.

Clear runs `--clear-tasks` on the Runner, which writes an empty list with a new revision under the same lock.

If a Runner has no list for an agent the Manager has tasks for (the agent moved there, or the Runner was rebuilt), the
Manager uploads its copy and `--restore-tasks` installs it under the lock with the same revision, unless the agent has
started a new list meanwhile. A failed restore is retried after 10 minutes; until then the Manager keeps its copy.
Finishing a move removes the list from the source Runner.

Whether you have seen a list is kept per browser (`dam-tasks-seen-<agent-id>` in local storage): the revision shown
when the dropdown or dialog was last closed. Any other revision shows the dot.

## Access

Viewing needs Read access to the agent (`GET /api/agents/{id}/tasks`); clearing needs read-write
(`DELETE /api/agents/{id}/tasks`, 409 while the Runner is not ready). The list is written by the agent, so the Portal
treats it as untrusted text.

## Verification

- `python3 DeveAgentManager.Tests/Desktop/dam-tools.test.py` (with `dam-tools.py` beside it, as in the test output
  folder; `RunnerDesktopTests` runs it): commands, revisions, bounds, durations, start times, collection, clear,
  restore and concurrent updates.
- `dotnet test --solution DeveAgentManager.slnx`: `AgentTasksTests` checks parsing and bounds, and collects, restores
  and clears over a real isolated SSH server; `UserAccessTests` checks read and read-write access.
- `node DeveAgentManager.Tests/Browser/agent-tasks.mjs` against a development Manager on `http://127.0.0.1:5099`
  (see [agent activity](agent-activity.md#verification) for the setup; the password there is test-only): the ring,
  the dot until the dropdown is closed and after reloads, task states and times, inline Clear for read-write only, the
  ⋮ menu dialog on a phone, and light and dark themes. Screenshots go to `/tmp`.
