# Agent shared files and images

Shared files and images belong to the publishing agent across all its sessions and harnesses. The terminal menus and
each saved session show the same collection. Existing publications appear automatically, without republishing or
copying their content. New publications update the other sessions' lists and badges as well.

Deleting a saved session removes its history but retains the living agent's collection, even if that was its only
session. A new session can still retrieve it. Deleting a shared file explicitly removes it from the agent collection
for everyone. After the agent is deleted, its archived sessions retain the collection until its last archive is
deleted. Continuing an archive of a deleted agent creates an independent copy, so new publications cannot leak back
into its old collection. Copies include only attachments the copying owner may read.

Agent owners and people with agent Read access can read the collection. A share of one conversation alone retains
access only to that conversation's original attachments; it does not reveal other sessions' publications. Download,
preview and delete routes continue to check the signed-in user's permissions. Read-only sharing does not allow
deleting files. Existing download ranges, inert previews, image validation and size limits remain in effect.

## Access from another agent

The tools use the requesting agent owner's current permissions. No subscription credentials or extra API keys are
needed. Names may be used when unambiguous; the returned agent IDs also work.

```sh
dam-tools list-artifacts
dam-tools list-artifacts --all
dam-tools list-artifacts --agent build-agent --offset 0 --limit 100
dam-tools fetch-artifact <artifact-id> --agent <source-agent-id>
dam-tools fetch-artifact <artifact-id> --agent build-agent --kind image --output /tmp/chart.png
```

Lists return JSON containing readable agents, artifact metadata, the total and the requested offset. The default page
contains up to 100 artifacts; the maximum is 200. Fetch prints a verified local path. Its default destination is a
private cache belonging to the requesting agent. An explicit output path must be new; existing files are never
overwritten. Files and images are transferred only on request, including between different runners. They are not
replicated into every agent's workspace or inserted as bytes into the conversation prompt.

The Manager automatically updates runner tools and installs a protected read capability for each existing agent.
New launches install it as well. Requests require that capability and a session belonging to that agent on the
requesting runner. The capability is bound to the agent, owner and runner; UUIDs alone cannot authorize a download.
The Manager checks the owner's current roles and source-agent sharing again on each request. Disabled owners,
deleted agents, changed ownership and removed shares therefore cannot keep reading through an old capability.
The runner's SSH account remains the trust boundary, as for the existing publication tools and harness logins.
Capabilities stay in private runtime files and must not be included in diagnostic output or tracked fixtures.

## Storage and transport

`/data/sessions/artifacts/<agent-id>.json` is a durable index of source attachment folders. Existing files under
`/data/sessions/files/<source-session-id>` and `images/<source-session-id>` remain where they are. Index updates use
atomic rename and complete before removing a source session or agent. No database migration or bulk history read is
needed. Metadata is loaded on demand; file content is streamed with bounded buffers. The index also preserves the
association after the database clears a deleted agent's workspace references.

Read requests use a private runner outbox collected in the existing file-collection poll. Empty queues do not launch
Python and add no additional SSH polling round trip. The Manager writes content first and an atomic JSON response
last. The tool checks size and SHA-256 before publishing a local path. Failed or interrupted reads cannot install a
partial file or launch agent work. Completed requests are removed by the tool; abandoned requests expire after six
hours. The default wait is five minutes and can be changed with `--timeout`.

## Regression checks

`AgentArtifactTests` covers old files and images, session deletion, new sessions, restart recovery, shared-conversation
isolation, revoked access, capability validation, wrong-runner requests, final archive cleanup and real SSH list/fetch
between agents assigned to different runners. `SessionFileApiTests` exercises cross-session lists, downloads,
previews, images, deletion and sharing through HTTP. The Python tool tests reject checksums that do not match,
truncation, excess bytes, symlinks, path traversal and overwrites. Existing format, transport, browser and background
job tests remain part of the full validation suite.
