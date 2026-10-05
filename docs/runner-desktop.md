# Desktop runners

Desktop runners use the VM's real graphical desktop. Setup enables automatic login for the runner account,
selects an installed X11 desktop session and installs display discovery, screenshots and input tools. Supported
login managers are LightDM, GDM/GDM3 and SDDM on Debian/Ubuntu. A desktop template must already contain a desktop
and an X11 session; setup reports missing or unsupported components rather than creating a virtual display.

## Existing runners

Update the Manager, then open the desktop runner and select **Operations → Re-run setup**. Setup version 2
advertises this update. It uses the existing runner account, repository folders and conversations. Repeated setup
preserves unrelated login-manager settings and keeps a backup of each original configuration file with the suffix
`.before-deveagentmanager`.

If only the greeter is running, setup restarts the display manager to apply autologin immediately. It does not
restart a logged-in graphical session or stop agents. An existing login therefore keeps its current desktop
backend until the next login or boot. If it is locked, unlock it once through the VM console. Desktop autostart
captures the current environment and disables idle blanking and locking on supported GNOME/Xfce desktops.

Restart agents to load the managed desktop instructions and initial graphical environment. Existing agents can
already use `~/.local/bin/dam-desktop run -- …` after setup without restarting. No repository instructions are
modified. A managed block is added to the harnesses' user-level instruction files, retaining custom instructions.

The runner page reports **Desktop ready**, **Login required**, **Setup required** or **Desktop unavailable**.
It refreshes with runner updates (at most once every 15 seconds) and has a manual refresh button. These are live
observations of the actual account's local session, not assumptions based on installed packages.

## Launch and test apps

```bash
dam-desktop status
dam-desktop run -- dotnet run --project MyAvaloniaApp
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
