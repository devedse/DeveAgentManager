import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("desktop", Path(__file__).with_name("desktop.py"))
desktop = importlib.util.module_from_spec(spec)
spec.loader.exec_module(desktop)


class DesktopTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def file(self, relative, contents=""):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(contents)
        return path

    def template(self, manager, session="xfce"):
        self.file("etc/X11/default-display-manager", "/usr/sbin/" + manager)
        self.file(f"usr/share/xsessions/{session}.desktop", "[Desktop Entry]\nName=Desktop\n")

    def test_lightdm_rerun_preserves_settings_and_original_backup(self):
        self.template("lightdm")
        path = self.file("etc/lightdm/lightdm.conf", "[Seat:*]\ngreeter-session=my-greeter\nuser-session=xfce\n[Other]\nValue=keep\n")
        original = path.read_text()
        desktop.configure(self.root, "runner")
        first = path.read_text()
        desktop.configure(self.root, "runner")
        self.assertEqual(first, path.read_text())
        self.assertEqual(original, path.with_name(path.name + ".before-deveagentmanager").read_text())
        self.assertIn("greeter-session = my-greeter", first)
        self.assertIn("Value = keep", first)
        self.assertIn("autologin-user = runner", first)
        self.assertIn("autologin-session = xfce", first)

    def test_template_desktop_choice_is_retained(self):
        self.template("lightdm", "mate")
        self.file("usr/share/xsessions/xfce.desktop")
        self.file("etc/lightdm/lightdm.conf", "[Seat:*]\nuser-session=mate\n")
        self.assertEqual(("lightdm", "mate"), desktop.configure(self.root, "runner"))

    def test_gdm_preserves_other_sections_and_selects_x11(self):
        self.template("gdm3", "gnome-xorg")
        self.file("etc/gdm3/daemon.conf", "[daemon]\nTimedLoginEnable=false\n[security]\nDisallowTCP=true\n")
        desktop.configure(self.root, "runner")
        text = (self.root / "etc/gdm3/daemon.conf").read_text()
        self.assertIn("AutomaticLogin = runner", text)
        self.assertIn("WaylandEnable = false", text)
        self.assertIn("DisallowTCP = true", text)
        self.assertIn("XSession = gnome-xorg", (self.root / "var/lib/AccountsService/users/runner").read_text())

    def test_sddm_uses_desktop_filename(self):
        self.template("sddm", "plasma")
        desktop.configure(self.root, "runner")
        text = (self.root / "etc/sddm.conf").read_text()
        self.assertIn("Session = plasma.desktop", text)
        self.assertIn("User = runner", text)

    def test_unknown_manager_and_missing_x11_do_not_change_login(self):
        self.template("unknown")
        with self.assertRaises(desktop.DesktopError):
            desktop.configure(self.root, "runner")
        self.assertFalse((self.root / "etc/lightdm/lightdm.conf").exists())
        self.file("etc/X11/default-display-manager", "/usr/sbin/lightdm")
        (self.root / "usr/share/xsessions/xfce.desktop").unlink()
        with self.assertRaisesRegex(desktop.DesktopError, "No X11"):
            desktop.configure(self.root, "runner")

    def test_removing_managed_instructions_preserves_custom_rules_and_links(self):
        block = desktop.BEGIN + "\nOld desktop rules\n" + desktop.END + "\n"
        paths = (".claude/CLAUDE.md", ".codex/AGENTS.md", ".pi/agent/AGENTS.md",
                 ".deveagentmanager/accounts/custom/codex/AGENTS.md")
        for path in paths:
            self.file(path, "My custom rules\n\n" + block + "More custom rules\n")
        account = self.root / ".deveagentmanager/accounts/linked/codex"
        account.mkdir(parents=True)
        linked = account / "AGENTS.md"
        linked.symlink_to(self.root / ".codex/AGENTS.md")
        login = self.file(".deveagentmanager/accounts/custom/codex/auth.json", "login untouched")
        desktop.remove_instructions(self.root)
        desktop.remove_instructions(self.root)
        for path in paths:
            text = (self.root / path).read_text()
            self.assertIn("My custom rules", text)
            self.assertIn("More custom rules", text)
            self.assertNotIn(desktop.BEGIN, text)
        self.assertTrue(linked.is_symlink())
        self.assertEqual("login untouched", login.read_text())

    def test_unmanaged_instructions_are_not_rewritten(self):
        path = self.file(".claude/CLAUDE.md", "My rules\n\n")
        desktop.remove_instructions(self.root)
        self.assertEqual("My rules\n\n", path.read_text())
        self.assertFalse((self.root / ".codex/AGENTS.md").exists())

    def test_logind_selects_only_active_local_runner_desktops(self):
        def fake(args, **kwargs):
            if args[1] == "list-sessions":
                return "a\nb\nc\nd\ne\nf"
            common = "User=123\nType=x11\nClass=user\nActive=yes\nRemote=no\n"
            return {
                "a": common,
                "b": common.replace("123", "456"),
                "c": common.replace("Class=user", "Class=greeter"),
                "d": common.replace("Remote=no", "Remote=yes"),
                "e": common.replace("Active=yes", "Active=no"),
                "f": common.replace("Type=x11", "Type=tty"),
            }[args[2]]
        with patch.object(desktop, "command", side_effect=fake):
            self.assertEqual(["a"], [s["Id"] for s in desktop.sessions(123)])

    def test_no_desktop_and_locked_desktop_have_actionable_status(self):
        with patch.object(desktop, "sessions", return_value=[]):
            self.assertEqual("login-required", desktop.status()["state"])
        with patch.object(desktop, "sessions", return_value=[{"LockedHint": "yes"}]):
            self.assertIn("locked", desktop.status()["message"])

    def candidates(self, values):
        uid = os.getuid()
        for i, env in enumerate(values):
            self.file(f"proc/{i + 100}/environ", "\0".join(k + "=" + v for k, v in env.items()))
            self.file(f"proc/{i + 100}/cgroup", "0::/user.slice/session-test.scope\n")
        self.file("x11/X4")
        self.file(f"runtime/{uid}/bus")
        return {"Id": "test", "Type": "x11"}, uid, self.root / "proc", self.root / "runtime", self.root / "x11"

    def test_discovers_current_display_and_keeps_unrelated_secrets_out(self):
        args = self.candidates([{"DISPLAY": ":4", "XAUTHORITY": "/run/test/auth", "XDG_SESSION_ID": "test", "API_KEY": "secret"}])
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "command", return_value=""):
            env = desktop.environment_for(*args)
        self.assertEqual(":4", env["DISPLAY"])
        self.assertEqual("/run/test/auth", env["XAUTHORITY"])
        self.assertNotIn("API_KEY", env)

    def test_ssh_forwarded_display_is_rejected(self):
        args = self.candidates([{"DISPLAY": "localhost:10.0", "XDG_SESSION_ID": "test"}])
        with patch.object(desktop, "FOLDER", self.root), self.assertRaises(desktop.DesktopError):
            desktop.environment_for(*args)

    def test_inaccessible_display_is_rejected(self):
        args = self.candidates([{"DISPLAY": ":4", "XDG_SESSION_ID": "test"}])
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "command", side_effect=subprocess.CalledProcessError(1, "xdpyinfo")):
            with self.assertRaises(desktop.DesktopError):
                desktop.environment_for(*args)

    def test_stale_capture_does_not_override_new_desktop(self):
        args = self.candidates([{"DISPLAY": ":4", "XDG_SESSION_ID": "test"}])
        self.file("desktop-session.json", json.dumps({"DISPLAY": ":99", "XDG_SESSION_ID": "test", "boot": "old"}))
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "command", return_value=""):
            self.assertEqual(":4", desktop.environment_for(*args)["DISPLAY"])

    def browser(self, display=":1"):
        self.file("browser-desktop.json", json.dumps({"display": display, "port": 6901}))
        self.file("x11/X1")
        return self.root / "x11"

    def test_browser_desktop_uses_its_display_and_this_boots_session_bus(self):
        x11 = self.browser()
        self.file("browser-desktop-session.json", json.dumps({"DISPLAY": ":1.0", "DBUS_SESSION_BUS_ADDRESS": "unix:path=/tmp/dbus-test",
                                                              "XDG_CURRENT_DESKTOP": "XFCE", "API_KEY": "secret", "boot": "this"}))
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "boot_id", return_value="this"), \
             patch.object(desktop, "command", return_value="") as run:
            env = desktop.browser_environment(desktop.browser_config(), os.getuid(), self.root / "runtime", x11)
        self.assertEqual(":1", env["DISPLAY"])
        self.assertEqual("x11", env["XDG_SESSION_TYPE"])
        self.assertEqual("unix:path=/tmp/dbus-test", env["DBUS_SESSION_BUS_ADDRESS"])
        self.assertNotIn("API_KEY", env)
        self.assertEqual(["xdpyinfo"], run.call_args.args[0])

    def test_browser_desktop_ignores_a_session_from_an_earlier_boot(self):
        x11 = self.browser()
        self.file("browser-desktop-session.json", json.dumps({"DISPLAY": ":1", "DBUS_SESSION_BUS_ADDRESS": "unix:path=/tmp/old", "boot": "old"}))
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "boot_id", return_value="this"), \
             patch.object(desktop, "command", return_value=""):
            env = desktop.browser_environment(desktop.browser_config(), os.getuid(), self.root / "runtime", x11)
        self.assertNotIn("DBUS_SESSION_BUS_ADDRESS", env)

    def test_browser_desktop_that_is_down_or_not_answering_says_what_to_do(self):
        self.file("browser-desktop.json", json.dumps({"display": ":1"}))
        with patch.object(desktop, "FOLDER", self.root), self.assertRaisesRegex(desktop.DesktopError, "isn't running"):
            desktop.browser_environment(desktop.browser_config(), os.getuid(), self.root / "runtime", self.root / "x11")
        x11 = self.browser()
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "command", side_effect=subprocess.CalledProcessError(1, "xdpyinfo")), \
             self.assertRaisesRegex(desktop.DesktopError, "starting"):
            desktop.browser_environment(desktop.browser_config(), os.getuid(), self.root / "runtime", x11)

    def test_browser_config_must_be_this_accounts_and_name_a_local_display(self):
        for display in ["localhost:1", ":1; id", "", 5]:
            self.file("browser-desktop.json", json.dumps({"display": display}))
            with patch.object(desktop, "FOLDER", self.root):
                self.assertIsNone(desktop.browser_config(), display)
        real = self.file("elsewhere.json", json.dumps({"display": ":1"}))
        (self.root / "browser-desktop.json").unlink()
        os.symlink(real, self.root / "browser-desktop.json")
        with patch.object(desktop, "FOLDER", self.root):
            self.assertIsNone(desktop.browser_config())

    def test_desktop_environment_prefers_the_browser_desktop_over_logind(self):
        self.browser()
        with patch.object(desktop, "FOLDER", self.root), patch.object(desktop, "browser_environment", return_value={"DISPLAY": ":1"}) as browser, \
             patch.object(desktop, "sessions") as sessions:
            self.assertEqual({"DISPLAY": ":1"}, desktop.desktop_environment())
        browser.assert_called_once()
        sessions.assert_not_called()

    def test_browser_login_is_recorded_without_a_logind_session_and_turns_compositing_off(self):
        environment = {"DAM_BROWSER_DESKTOP": "1", "DISPLAY": ":1", "DBUS_SESSION_BUS_ADDRESS": "unix:path=/tmp/dbus-test", "XDG_SESSION_TYPE": "x11"}
        with patch.dict(os.environ, environment, clear=True), patch.object(desktop, "FOLDER", self.root), \
             patch.object(desktop, "boot_id", return_value="this"), patch.object(desktop.shutil, "which", return_value="/usr/bin/tool"), \
             patch.object(desktop, "command", return_value="") as run:
            desktop.capture_session()
        saved = json.loads((self.root / "browser-desktop-session.json").read_text())
        self.assertEqual((":1", "this"), (saved["DISPLAY"], saved["boot"]))
        self.assertFalse((self.root / "desktop-session.json").exists())
        self.assertIn(["xfconf-query", "-c", "xfwm4", "-p", "/general/use_compositing", "-n", "-t", "bool", "-s", "false"],
                      [call.args[0] for call in run.call_args_list])

    def test_vm_login_still_needs_its_logind_session_and_keeps_its_compositor(self):
        with patch.dict(os.environ, {"DISPLAY": ":0"}, clear=True), patch.object(desktop, "FOLDER", self.root), \
             self.assertRaises(desktop.DesktopError):
            desktop.capture_session()
        with patch.dict(os.environ, {"DISPLAY": ":0", "XDG_SESSION_ID": "4"}, clear=True), patch.object(desktop, "FOLDER", self.root), \
             patch.object(desktop, "boot_id", return_value="this"), patch.object(desktop.shutil, "which", return_value="/usr/bin/tool"), \
             patch.object(desktop, "command", return_value="") as run:
            desktop.capture_session()
        self.assertTrue((self.root / "desktop-session.json").exists())
        self.assertNotIn("xfwm4", [arg for call in run.call_args_list for arg in call.args[0]])

    def test_run_refreshes_environment_and_preserves_arguments(self):
        with patch.object(sys, "argv", ["dam-desktop", "run", "--", "app", "a b", "$(touch unwanted)"]), \
             patch.object(desktop, "desktop_environment", return_value={"DISPLAY": ":4", "XDG_SESSION_TYPE": "x11"}), \
             patch.object(os, "execvpe", side_effect=SystemExit) as execute:
            with self.assertRaises(SystemExit):
                desktop.main()
        self.assertEqual(["app", "a b", "$(touch unwanted)"], execute.call_args.args[1])
        self.assertEqual(":4", execute.call_args.args[2]["DISPLAY"])

    def test_ui_text_is_passed_as_literal_argument(self):
        with patch.object(desktop, "command", return_value="") as run:
            desktop.action({"action": "type", "text": "-- $(touch unwanted)"}, {"DISPLAY": ":4"})
        self.assertEqual("-- $(touch unwanted)", run.call_args.args[0][-1])
        self.assertEqual("--", run.call_args.args[0][-2])

    def test_invalid_actions_are_rejected_before_running(self):
        with patch.object(desktop, "command") as run:
            for item in [{"action": "click", "x": -1, "y": 2}, {"action": "key", "keys": "--window"},
                         {"action": "focus", "window": "$(id)"}, {"action": "wait", "seconds": -1}, {"action": "shell"}]:
                with self.assertRaises(desktop.DesktopError):
                    desktop.action(item, {})
            run.assert_not_called()

    def test_batches_hold_the_input_lock_across_all_actions(self):
        batch = self.file("batch.json", json.dumps([{"action": "key", "keys": "Return"}, {"action": "screenshot", "path": "/tmp/test.png"}]))
        order = []
        class Lock:
            def __enter__(self): order.append("locked")
            def __exit__(self, *args): order.append("unlocked")
        with patch.object(sys, "argv", ["dam-desktop", "batch", str(batch)]), \
             patch.object(desktop, "desktop_environment", return_value={"XDG_SESSION_TYPE": "x11"}), \
             patch.object(desktop, "input_lock", return_value=Lock()), \
             patch.object(desktop, "action", side_effect=lambda item, env: order.append(item["action"])):
            desktop.main()
        self.assertEqual(["locked", "key", "screenshot", "unlocked"], order)


if __name__ == "__main__":
    unittest.main()
