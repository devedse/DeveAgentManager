#!/usr/bin/env python3
"""Connect to the runner account's real desktop; never create a virtual display."""
import argparse
import configparser
import contextlib
import fcntl
import json
import os
from pathlib import Path
import pwd
import re
import shlex
import shutil
import stat
import subprocess
import sys
import time

KEYS = ("DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR",
        "DBUS_SESSION_BUS_ADDRESS", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "XDG_SESSION_ID")
FOLDER = Path.home() / ".deveagentmanager"
BEGIN = "<!-- DeveAgentManager desktop -->"
END = "<!-- /DeveAgentManager desktop -->"
INSTRUCTIONS = """This runner has a real shared desktop, visible through the VM console.
Use `dam-desktop status` to check it. Launch GUI apps with `dam-desktop run -- <command>`
(for example `dam-desktop run -- dotnet run --project MyApp`). This discovers fresh display
credentials every time. Do not install or start Xvfb or a separate desktop to display an app.
Use `dam-desktop screenshot /tmp/desktop.png`, then your image-reading tool to inspect it.
Use `dam-desktop windows`, `click X Y [BUTTON]`, `type TEXT`, `key ctrl+l`, and `focus WINDOW_ID`.
For a sequence of actions use `dam-desktop batch /tmp/actions.json`: a JSON array of objects
such as {"action":"click","x":100,"y":200}, {"action":"type","text":"hello"},
{"action":"key","keys":"Return"}, {"action":"wait","seconds":1},
{"action":"screenshot","path":"/tmp/desktop.png"}. A batch holds the shared input lock
so another agent cannot interleave clicks and typing. All agents on this runner share the
desktop: avoid moving or closing another agent's windows. Check `dam-desktop --help` for details.
If the desktop is unavailable, report the status; do not claim that the UI was tested.
"""


class DesktopError(Exception):
    def __init__(self, message, state="unavailable"):
        super().__init__(message)
        self.state = state


def command(args, env=None, timeout=5):
    return subprocess.run(args, env=env, text=True, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, timeout=timeout, check=True).stdout.strip()


def properties(text):
    return dict(line.split("=", 1) for line in text.splitlines() if "=" in line)


def sessions(uid):
    result = []
    for line in command(["loginctl", "list-sessions", "--no-legend", "--no-pager"]).splitlines():
        sid = line.split()[0]
        try:
            info = properties(command(["loginctl", "show-session", sid, "--no-pager",
                "-p", "User", "-p", "Type", "-p", "Class", "-p", "Active",
                "-p", "Remote", "-p", "LockedHint", "-p", "Leader"]))
            if (info.get("User") == str(uid) and info.get("Class") == "user"
                    and info.get("Type") in ("x11", "wayland")
                    and info.get("Active") == "yes" and info.get("Remote") == "no"):
                info["Id"] = sid
                result.append(info)
        except (subprocess.SubprocessError, OSError):
            continue
    return result


def environment_for(session, uid, procroot=Path("/proc"), runtime_root=Path("/run/user"), x11root=Path("/tmp/.X11-unix")):
    """Read only this user's current logind session, excluding SSH and greeter environments."""
    candidates = []
    record = FOLDER / "desktop-session.json"
    try:
        if record.lstat().st_uid == uid and not record.is_symlink():
            saved = json.loads(record.read_text())
            if isinstance(saved, dict) and saved.get("XDG_SESSION_ID") == session["Id"] and saved.get("boot") == Path("/proc/sys/kernel/random/boot_id").read_text().strip():
                candidates.append(saved)
    except (OSError, ValueError):
        pass
    for proc in procroot.iterdir():
        if not proc.name.isdigit():
            continue
        try:
            if proc.stat().st_uid != uid:
                continue
            values = dict(item.split("=", 1) for item in (proc / "environ").read_bytes().decode(errors="replace").split("\0") if "=" in item)
            if (values.get("XDG_SESSION_ID") != session["Id"]
                    and f"/session-{session['Id']}.scope" not in (proc / "cgroup").read_text()):
                continue
            if values.get("DISPLAY") or values.get("WAYLAND_DISPLAY"):
                candidates.append(values)
        except (OSError, ValueError):
            continue
    for values in candidates:
        env = {k: values[k] for k in KEYS if values.get(k)}
        env["XDG_SESSION_ID"] = session["Id"]
        env["XDG_SESSION_TYPE"] = session["Type"]
        runtime = runtime_root / str(uid)
        if runtime.is_dir() and runtime.stat().st_uid == uid:
            env["XDG_RUNTIME_DIR"] = str(runtime)
            if (runtime / "bus").exists():
                env["DBUS_SESSION_BUS_ADDRESS"] = f"unix:path={runtime}/bus"
        # Never attach to an SSH-forwarded or remote X server.
        display = re.fullmatch(r":([0-9]+)(?:\.[0-9]+)?", env.get("DISPLAY", ""))
        if not display or not (x11root / ("X" + display[1])).exists():
            env.pop("DISPLAY", None)
            env.pop("XAUTHORITY", None)
        elif "XAUTHORITY" not in env and (Path.home() / ".Xauthority").is_file():
            env["XAUTHORITY"] = str(Path.home() / ".Xauthority")
        if env.get("DISPLAY"):
            try:
                command(["xdpyinfo"], env={**os.environ, **env}, timeout=3)
            except (OSError, subprocess.SubprocessError):
                continue
        elif session["Type"] == "wayland":
            socket = env.get("WAYLAND_DISPLAY", "")
            if not socket or "/" in socket or not runtime.joinpath(socket).exists():
                continue
        else:
            continue
        return env
    raise DesktopError("The desktop is logged in, but its display connection is unavailable. Re-run setup or log in again.")


def desktop_environment():
    active = sessions(os.getuid())
    if not active:
        raise DesktopError("No local desktop is logged in as the runner account. Log in through the VM console; autologin applies on the next boot.", "login-required")
    # With more than one seat, use the first available unlocked desktop.
    for session in active:
        if session.get("LockedHint") == "yes":
            continue
        try:
            return environment_for(session, os.getuid())
        except DesktopError:
            pass
    if all(s.get("LockedHint") == "yes" for s in active):
        raise DesktopError("The desktop is locked. Unlock it through the VM console.", "login-required")
    raise DesktopError("The desktop session exists, but its display is unavailable. Re-run setup or log in again.")


def status():
    try:
        env = desktop_environment()
        tools = all(shutil.which(t) for t in ("scrot", "xdotool", "wmctrl"))
        automation = env["XDG_SESSION_TYPE"] == "x11" and tools
        return dict(state="ready", message="Desktop ready" if automation else "Desktop connected; full UI interaction requires an X11 login.",
                    sessionType=env["XDG_SESSION_TYPE"], display=env.get("DISPLAY"), automationAvailable=automation)
    except DesktopError as ex:
        return dict(state=ex.state, message=str(ex), automationAvailable=False)


def capture_session():
    values = {k: os.environ[k] for k in KEYS if os.environ.get(k)}
    if not values.get("XDG_SESSION_ID"):
        raise DesktopError("Desktop login did not supply XDG_SESSION_ID.")
    values["boot"] = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
    FOLDER.mkdir(mode=0o700, exist_ok=True)
    temporary = FOLDER / f"desktop-session-{os.getpid()}.json"
    with temporary.open("x") as out:
        os.chmod(temporary, 0o600)
        json.dump(values, out)
    temporary.replace(FOLDER / "desktop-session.json")
    # Dedicated development desktops should stay visible for unattended UI tests.
    settings = [
        ["xset", "s", "off"], ["xset", "-dpms"],
        ["gsettings", "set", "org.gnome.desktop.session", "idle-delay", "0"],
        ["gsettings", "set", "org.gnome.desktop.screensaver", "lock-enabled", "false"],
        ["gsettings", "set", "org.gnome.settings-daemon.plugins.power", "sleep-inactive-ac-type", "nothing"],
    ]
    for channel, name in [("xfce4-power-manager", "/xfce4-power-manager/dpms-enabled"),
                          ("xfce4-screensaver", "/saver/enabled"), ("xfce4-screensaver", "/lock/enabled")]:
        settings.append(["xfconf-query", "-c", channel, "-p", name, "-n", "-t", "bool", "-s", "false"])
    for args in settings:
        if shutil.which(args[0]):
            try:
                command(args)
            except (OSError, subprocess.SubprocessError):
                pass


def write_config(path, changes):
    """Preserve unrelated settings and back up the original once, even on repeated setup."""
    config = configparser.ConfigParser(interpolation=None, strict=False)
    config.optionxform = str
    if path.exists():
        config.read(path)
    for section, values in changes.items():
        if not config.has_section(section):
            config.add_section(section)
        for key, value in values.items():
            config.set(section, key, value)
    path.parent.mkdir(parents=True, exist_ok=True)
    backup = path.with_name(path.name + ".before-deveagentmanager")
    if path.exists() and not backup.exists():
        shutil.copy2(path, backup)
    temporary = path.with_name(path.name + f".dam-{os.getpid()}")
    try:
        with temporary.open("x") as out:
            config.write(out)
        if path.exists():
            original = path.stat()
            os.chmod(temporary, stat.S_IMODE(original.st_mode))
            if os.geteuid() == 0:
                os.chown(temporary, original.st_uid, original.st_gid)
        else:
            os.chmod(temporary, 0o644)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def configure(root, user):
    """Root-only display-manager configuration. root is injectable for filesystem tests."""
    if not re.fullmatch(r"[a-zA-Z_][a-zA-Z0-9_.-]*\$?", user):
        raise DesktopError("Invalid runner account name.")
    manager_file = root / "etc/X11/default-display-manager"
    manager = Path(manager_file.read_text().strip()).name if manager_file.exists() else ""
    if not manager:
        service = root / "etc/systemd/system/display-manager.service"
        if service.is_symlink():
            manager = Path(os.readlink(service)).stem
    if manager not in ("lightdm", "gdm", "gdm3", "sddm"):
        raise DesktopError(f"Unsupported desktop login manager '{manager or 'not found'}'. Supported: LightDM, GDM and SDDM.")
    choices = sorted((root / "usr/share/xsessions").glob("*.desktop"))
    if not choices:
        raise DesktopError("No X11 desktop session is installed. Install the desktop's X11 session, then re-run setup.")
    # Prefer the template's configured desktop when it has an X11 variant.
    preferred = []
    if manager in ("lightdm", "sddm"):
        current = configparser.ConfigParser(interpolation=None, strict=False)
        current.read(root / ("etc/lightdm/lightdm.conf" if manager == "lightdm" else "etc/sddm.conf"))
        preferred.append(current.get("Seat:*", "autologin-session", fallback="") if manager == "lightdm"
                         else current.get("Autologin", "Session", fallback="").removesuffix(".desktop"))
        if manager == "lightdm":
            preferred.append(current.get("Seat:*", "user-session", fallback=""))
    account = root / "var/lib/AccountsService/users" / user
    if account.exists():
        c = configparser.ConfigParser(interpolation=None)
        c.read(account)
        preferred.append(c.get("User", "XSession", fallback=""))
    preferred += ["xfce", "xubuntu", "gnome-xorg", "plasma", "cinnamon", "mate", "LXDE"]
    selected = next((p for name in preferred for p in choices if p.stem == name), choices[0])
    if manager == "lightdm":
        write_config(root / "etc/lightdm/lightdm.conf", {"Seat:*": {
            "autologin-user": user, "autologin-user-timeout": "0", "autologin-session": selected.stem}})
    elif manager in ("gdm", "gdm3"):
        folder = root / "etc" / manager
        path = folder / ("daemon.conf" if (folder / "daemon.conf").exists() else "custom.conf")
        write_config(path, {"daemon": {"AutomaticLoginEnable": "true", "AutomaticLogin": user, "WaylandEnable": "false"}})
        write_config(account, {"User": {"XSession": selected.stem}})
    else:
        write_config(root / "etc/sddm.conf", {"Autologin": {"User": user, "Session": selected.name, "Relogin": "true"}})
    return manager, selected.stem


def install_instructions(home):
    paths = [home / relative for relative in (".codex/AGENTS.md", ".claude/CLAUDE.md", ".pi/agent/AGENTS.md")]
    global_codex = paths[0]
    # Existing accounts skip account setup when their credentials have not changed.
    # Repair their instruction link here without replacing custom account instructions.
    for account in (home / ".deveagentmanager/accounts").glob("*/codex"):
        path = account / "AGENTS.md"
        if not path.exists() and not path.is_symlink():
            path.symlink_to(global_codex)
        elif not path.is_symlink():
            paths.append(path)
    for path in paths:
        path.parent.mkdir(parents=True, exist_ok=True)
        old = path.read_text() if path.exists() else ""
        old = re.sub(re.escape(BEGIN) + r".*?" + re.escape(END) + r"\n?", "", old, flags=re.S).rstrip()
        path.write_text(old + ("\n\n" if old else "") + BEGIN + "\n" + INSTRUCTIONS + END + "\n")


@contextlib.contextmanager
def input_lock():
    FOLDER.mkdir(mode=0o700, exist_ok=True)
    with (FOLDER / "desktop-input.lock").open("a") as lock:
        deadline = time.monotonic() + 30
        while True:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    raise DesktopError("Another agent is using the desktop. Try again after its batch finishes.")
                time.sleep(0.1)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def action(item, env):
    kind = item.get("action")
    if kind == "click":
        x, y, button = int(item["x"]), int(item["y"]), int(item.get("button", 1))
        if not (0 <= x <= 100000 and 0 <= y <= 100000 and 1 <= button <= 9):
            raise DesktopError("Invalid click coordinates or mouse button.")
        args = ["xdotool", "mousemove", "--sync", str(x), str(y), "click", str(button)]
    elif kind == "type":
        args = ["xdotool", "type", "--clearmodifiers", "--delay", "12", "--", str(item["text"])]
    elif kind == "key":
        key = str(item["keys"])
        if not re.fullmatch(r"[A-Za-z0-9_+]+", key):
            raise DesktopError("Use a key combination such as ctrl+l, Return or Escape.")
        args = ["xdotool", "key", "--clearmodifiers", key]
    elif kind == "focus":
        window = str(item["window"])
        if not re.fullmatch(r"(?:0x[0-9a-fA-F]+|[0-9]+)", window):
            raise DesktopError("Invalid window ID.")
        args = ["xdotool", "windowactivate", "--sync", window]
    elif kind == "screenshot":
        path = Path(item["path"]).expanduser().absolute()
        command(["scrot", "-o", str(path)], env, timeout=10)
        return str(path)
    elif kind == "wait":
        seconds = float(item["seconds"])
        if not 0 <= seconds <= 10:
            raise DesktopError("Batch waits must be between 0 and 10 seconds.")
        time.sleep(seconds)
        return ""
    else:
        raise DesktopError(f"Unknown desktop action: {kind}")
    return command(args, env, timeout=30)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="mode", required=True)
    sub.add_parser("status", help="Check the real desktop and UI tools (JSON)")
    sub.add_parser("env", help="Print shell-quoted session exports")
    sub.add_parser("session-start", help="Capture the desktop login environment")
    sub.add_parser("install-instructions", help="Update managed instructions without replacing user instructions")
    configure_parser = sub.add_parser("configure", help="Configure autologin as root")
    configure_parser.add_argument("user")
    run = sub.add_parser("run", help="Launch an app using the latest desktop connection")
    run.add_argument("command", nargs=argparse.REMAINDER)
    sub.add_parser("windows", help="List shared desktop windows")
    screenshot = sub.add_parser("screenshot", help="Save a PNG of the actual desktop")
    screenshot.add_argument("path")
    click = sub.add_parser("click", help="Move and click")
    click.add_argument("x", type=int)
    click.add_argument("y", type=int)
    click.add_argument("button", type=int, nargs="?", default=1)
    typing = sub.add_parser("type", help="Type literal text into the focused window")
    typing.add_argument("text")
    key = sub.add_parser("key", help="Send a key combination")
    key.add_argument("keys")
    focus = sub.add_parser("focus", help="Activate a window by ID")
    focus.add_argument("window")
    batch = sub.add_parser("batch", help="Run a JSON array of actions with exclusive desktop input")
    batch.add_argument("path")
    args = parser.parse_args()
    if args.mode == "configure":
        if os.geteuid() != 0:
            raise DesktopError("Autologin configuration requires root.")
        pwd.getpwnam(args.user)
        print("Configured %s autologin with X11 session %s" % configure(Path("/"), args.user))
        return
    if args.mode == "install-instructions":
        install_instructions(Path.home())
        return
    if args.mode == "session-start":
        capture_session()
        return
    if args.mode == "status":
        print(json.dumps(status()))
        return
    env = desktop_environment()
    if args.mode == "env":
        print("\n".join(f"export {k}={shlex.quote(v)}" for k, v in env.items()))
        return
    env = {**os.environ, **env}
    if args.mode == "run":
        argv = args.command[1:] if args.command[:1] == ["--"] else args.command
        if not argv:
            parser.error("run needs a command after --")
        os.execvpe(argv[0], argv, env)
    if env["XDG_SESSION_TYPE"] != "x11":
        raise DesktopError("UI interaction requires an X11 desktop login. Setup configures X11 for the next login; launch apps with 'run' on either backend.")
    with input_lock():
        if args.mode == "windows":
            print(command(["wmctrl", "-lpG"], env))
        elif args.mode == "batch":
            items = json.loads(Path(args.path).read_text())
            if not isinstance(items, list) or not 1 <= len(items) <= 100:
                raise DesktopError("A batch must contain 1 to 100 actions.")
            for item in items:
                if not isinstance(item, dict):
                    raise DesktopError("Each batch action must be an object.")
                output = action(item, env)
                if output:
                    print(output)
        else:
            print(action({**vars(args), "action": args.mode}, env))


if __name__ == "__main__":
    try:
        main()
    except (DesktopError, OSError, ValueError, KeyError, subprocess.SubprocessError) as ex:
        print(f"Desktop: {ex}", file=sys.stderr)
        sys.exit(1)
