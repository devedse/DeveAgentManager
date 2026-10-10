# Desktop runners

Desktop runners use the VM's real graphical desktop. Setup enables automatic login for the runner account,
selects an installed X11 desktop session and installs display discovery, screenshots and input tools. Supported
login managers are LightDM, GDM/GDM3 and SDDM on Debian/Ubuntu. A desktop template must already contain a desktop
and an X11 session; setup reports missing or unsupported components rather than creating a virtual display.

## Existing runners

Update the Manager, then open the desktop runner and select **Operations → Re-run setup**. Setup version 3
advertises this update. It uses the existing runner account, repository folders and conversations. Repeated setup
preserves unrelated login-manager settings and keeps a backup of each original configuration file with the suffix
`.before-deveagentmanager`.

If only the greeter is running, setup restarts the display manager to apply autologin immediately. It does not
restart a logged-in graphical session or stop agents. An existing login therefore keeps its current desktop
backend until the next login or boot. If it is locked, unlock it once through the VM console. Desktop autostart
captures the current environment and disables idle blanking and locking on supported GNOME/Xfce desktops.

Restart existing agents once to load the environment hook. Normal Bash commands, including `bash -lc`, refresh
the graphical environment before execution, so `dotnet run` opens an Avalonia app on the real desktop. Nested Bash
commands refresh again after a desktop restart. An existing `BASH_ENV` hook is preserved. A managed preamble in
`.bashrc` also handles Bash's SSH startup path, before its noninteractive early return. Programs that spawn GUI
processes directly without starting a shell retain their process's original environment; `dam-desktop run -- …`
is available for that case. Missing or locked desktops clear stale graphical settings.

No harness instructions are needed for the desktop. Re-run setup removes only our previously managed desktop
blocks from `CLAUDE.md` and the other harness instruction files; custom content and account links remain intact.

The runner page reports **Desktop ready**, **Login required**, **Setup required** or **Desktop unavailable**.
It refreshes with runner updates (at most once every 15 seconds) and has a manual refresh button. These are live
observations of the actual account's local session, not assumptions based on installed packages.

## Launch and test apps

```bash
dam-desktop status
dotnet run --project MyAvaloniaApp
dam-desktop screenshot /tmp/app.png
dam-desktop windows
dam-desktop focus 0x0400001
dam-desktop click 100 200
dam-desktop type 'Hello world'
dam-desktop key Return
```

Read the screenshot with the harness's image-reading tool. Display discovery validates an active local logind
session for the runner UID and an authenticated X connection. It excludes greeters, other users and SSH-forwarded
displays. Each invocation discovers current credentials again, including after desktop restarts. Wayland sessions
can launch apps, but full screenshot and input automation requires the X11 login configured by setup. No display
authorization is disabled or exposed through the API.

All agents on a runner share the desktop. Each input command takes a per-user lock; use a batch to keep multiple
steps together so another agent cannot interleave its input:

```json
[
  {"action": "focus", "window": "0x0400001"},
  {"action": "click", "x": 100, "y": 200},
  {"action": "type", "text": "Hello world"},
  {"action": "key", "keys": "Return"},
  {"action": "wait", "seconds": 1},
  {"action": "screenshot", "path": "/tmp/app.png"}
]
```

Save this as `/tmp/actions.json` and run `dam-desktop batch /tmp/actions.json`. This coordinates input, but does
not isolate windows or prevent applications themselves from changing focus. The normal VM console remains the
place to watch and interact with the shared desktop.

## View the desktop in the Manager

Use **Desktop** on a runner or **Runner desktop** in an agent's ⋮ menu. It initially
shows a snapshot. **Live** refreshes every two seconds, or every five seconds on slower connections. Polling stops
while the page is hidden and on navigation. Captures do not overlap; concurrent viewers share recent frames.
The viewer is read-only and labels the last successful capture in the browser's local time. It shows the whole
runner desktop, including other agents' windows.

**Save snapshot** stores the desktop image with the selected conversation. Temporary frames are removed from
the runner after transfer and expire from the Manager's memory after two seconds. Viewing requires a logged-in
Manager session and an available X11 desktop.

## Agent tools (`dam-tools`)

`dam-tools` is installed on both desktop and headless runners (`~/.local/bin/dam-tools`; the source is
`DeveAgentManager.Core/Scripts/dam-tools.py`, Python 3 without dependencies). New commands go there as subcommands.
`dam-tools --help` lists them.

### Share images

```bash
dam-tools share-image /tmp/screenshot.png --caption "Updated Avalonia interface"
```

The tool inherits the workspace and conversation IDs from the agent's launch environment. It takes a durable
copy of the file, and the Manager collects it in the background, even when no browser is open. Outside an agent,
supply `--agent <UUID> --session <UUID>`. PNG, JPEG, GIF and WebP images up to 10 MiB are supported; SVG is excluded.

Images appear under **Shared images** in the agent toolbar and conversation page. Tap an image to enlarge or
download it. Images stay with the conversation across harness/session switches and runner deletion. Deleting the
conversation removes its saved images. After successful import, the copy queued on the runner is removed.

### Share files

```bash
dam-tools share-file build/app-release.apk --caption "Debug build with the new login screen"
```

For anything the user should download rather than look at: an APK, an installer, an archive or a report. Any
regular file up to 1 GiB; archive a folder first. Like images, the tool takes a durable copy (so the original can be
deleted), records its size and SHA-256, and the Manager collects it in the background over SFTP, streaming it to disk.
Every runner is collected on its own, so a large transfer doesn't hold up the others. The Manager checks the size and
checksum before the file appears, and drops a copy that changed after it was shared.

Files appear under **Shared files**: a toolbar button with a count once a file exists (in the ⋮ menu when the
toolbar has no room) and a
panel on the conversation page. Each shows its name, caption, size, age and SHA-256. Downloads are always
attachments that are never shown in the page, and they can be resumed. An APK downloads as an Android package, so the
phone offers to install it. Anyone who can read the conversation can download its files; its owner can delete one
for everyone. Files stay with the conversation across harness/session switches and runner deletion, are copied along
when someone continues a copy of the conversation, and are removed when the conversation is deleted. They are stored
in the Manager's data folder (`sessions/files`).

### How agents know about it

The Manager adds a few sentences about `dam-tools` (sharing images and files) to the system prompt of each harness it starts
(`--append-system-prompt` for Claude Code and pi, `-c developer_instructions=…` for Codex). Nothing is written to
`CLAUDE.md`, `AGENTS.md` or any other file, so it can't end up in a repository, and a harness started by hand over
SSH doesn't get it. The harnesses record the system prompt when a conversation starts, so conversations started
before a runner had `dam-tools` keep the prompt they started with. Runners get the sentences once Re-run setup has
installed `dam-tools` (setup script version 4).

### Keeping it current

After that, the Manager keeps `dam-tools` current by itself: shortly after it starts (and after each setup) it
compares the checksum of each Ready runner's copy with its own and replaces a different copy in one rename, so new
commands work without re-running setup. An agent that is running `dam-tools` at that moment finishes with the old copy.
