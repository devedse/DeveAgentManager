#!/usr/bin/env bash
# Desktop-only setup, also used by Re-run setup. Leave existing desktop sessions and agents running.
set -euo pipefail
if [ "$(id -u)" = 0 ]; then
  echo 'ERROR: desktop runners need a non-root runner account.' >&2
  exit 10
fi
sudo -n true
find "$HOME/.deveagentmanager" -maxdepth 1 -name 'desktop-frame-*.jpg' -type f -mmin +5 -delete
echo '==> Installing desktop connection and UI tools'
sudo -n env DEBIAN_FRONTEND=noninteractive apt-get -q -y \
  -o DPkg::Lock::Timeout=600 -o Dpkg::Options::=--force-confdef \
  -o Dpkg::Options::=--force-confold install --no-install-recommends \
  python3 x11-utils x11-xserver-utils xdotool scrot wmctrl

echo '==> Configuring desktop autologin'
sudo -n python3 "$HOME/.deveagentmanager/desktop.py" configure "$(id -un)"
if getent group autologin >/dev/null; then sudo -n usermod -a -G autologin "$(id -un)"; fi
mkdir -p "$HOME/.local/bin" "$HOME/.config/autostart"
ln -sf "$HOME/.deveagentmanager/desktop.py" "$HOME/.local/bin/dam-desktop"
python3 - <<'PY'
from pathlib import Path
path = str(Path.home() / '.deveagentmanager/desktop.py')
escaped = path.replace('\\', '\\\\').replace('"', '\\"').replace('`', '\\`').replace('$', '\\$')
(Path.home() / '.config/autostart/deveagentmanager-desktop.desktop').write_text(
    '[Desktop Entry]\nType=Application\nName=DeveAgentManager desktop connection\n'
    f'Exec=python3 "{escaped}" session-start\nTerminal=false\nX-GNOME-Autostart-enabled=true\n')
# Bash started by SSH may read .bashrc instead of BASH_ENV. Install before the
# distro's noninteractive early return, retaining the rest of the user's file.
import re
rc = Path.home() / '.bashrc'
old = rc.read_text() if rc.exists() else ''
begin = '# >>> DeveAgentManager desktop shell >>>'
end = '# <<< DeveAgentManager desktop shell <<<'
old = re.sub(re.escape(begin) + r'.*?' + re.escape(end) + r'\n?', '', old, flags=re.S)
block = '''# >>> DeveAgentManager desktop shell >>>
if [ -f "$HOME/.deveagentmanager/desktop-shell.sh" ]; then
  if [ "${BASH_ENV:-}" != "$HOME/.deveagentmanager/desktop-shell.sh" ]; then
    export DAM_PREVIOUS_BASH_ENV="${BASH_ENV:-}"
  fi
  . "$HOME/.deveagentmanager/desktop-shell.sh"
fi
# <<< DeveAgentManager desktop shell <<<
'''
rc.write_text(block + old)
PY
cat > "$HOME/.deveagentmanager/desktop-env.sh" <<'EOF'
# Refresh only display settings; do not evaluate any session file as shell code.
if [ -x "$HOME/.deveagentmanager/desktop.py" ]; then
  dam_desktop_exports=$(python3 "$HOME/.deveagentmanager/desktop.py" env 2>/dev/null) || dam_desktop_exports=""
  unset DISPLAY XAUTHORITY WAYLAND_DISPLAY XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS XDG_SESSION_TYPE XDG_CURRENT_DESKTOP XDG_SESSION_ID
  if [ -n "$dam_desktop_exports" ]; then eval "$dam_desktop_exports"; fi
  unset dam_desktop_exports
fi
EOF
chmod 600 "$HOME/.deveagentmanager/desktop-env.sh"
python3 "$HOME/.deveagentmanager/desktop.py" remove-instructions

# A running desktop must not be disconnected by Re-run setup. If only the greeter is running,
# restart it to apply autologin now; any user's existing graphical session prevents a restart.
desktop_logged_in=0
while read -r sid rest; do
  [ -n "$sid" ] || continue
  type=$(loginctl show-session "$sid" -p Type --value 2>/dev/null || true)
  class=$(loginctl show-session "$sid" -p Class --value 2>/dev/null || true)
  if { [ "$type" = x11 ] || [ "$type" = wayland ]; } && [ "$class" = user ]; then desktop_logged_in=1; fi
done < <(loginctl list-sessions --no-legend --no-pager)
if [ "$desktop_logged_in" = 0 ]; then
  echo '==> Starting the automatic desktop login'
  sudo -n systemctl restart display-manager
else
  echo 'Existing desktop left running. Autologin and X11 settings apply on the next login or boot.'
fi
for attempt in $(seq 1 15); do
  desktop_status=$(python3 "$HOME/.deveagentmanager/desktop.py" status)
  if python3 -c 'import json,sys;sys.exit(json.load(sys.stdin)["state"] != "ready")' <<< "$desktop_status"; then
    . "$HOME/.deveagentmanager/desktop-env.sh"
    python3 "$HOME/.deveagentmanager/desktop.py" session-start
    echo "$desktop_status"
    exit 0
  fi
  sleep 2
done
echo "$desktop_status"
echo 'Desktop tools installed. Log in through the VM console if the automatic login has not started yet.'
