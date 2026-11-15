#!/usr/bin/env bash
# Desktop-only setup, also used by Re-run setup. Leave existing desktop sessions and agents running.
#   desktop-setup.sh          the VM's own screen: automatic login to its desktop (LightDM, GDM or SDDM)
#   desktop-setup.sh browser  a browser desktop for Runners without a screen (containers): XFCE on KasmVNC
set -euo pipefail
mode=${1:-screen}
if [ "$(id -u)" = 0 ]; then
  echo 'ERROR: desktop runners need a non-root runner account.' >&2
  exit 10
fi
sudo -n true
find "$HOME/.deveagentmanager" -maxdepth 1 -name 'desktop-frame-*.jpg' -type f -mmin +5 -delete
apt_install() {
  sudo -n env DEBIAN_FRONTEND=noninteractive apt-get -q -y \
    -o DPkg::Lock::Timeout=600 -o Dpkg::Options::=--force-confdef \
    -o Dpkg::Options::=--force-confold install --no-install-recommends "$@"
}
echo '==> Installing desktop connection and UI tools'
apt_install python3 x11-utils x11-xserver-utils xdotool scrot wmctrl

# KasmVNC's own packages, pinned and checked: an X server with a web viewer, and a GPU mode (DRI3) on the render node.
kasmvnc_version=1.5.2
kasmvnc_package() {
  . /etc/os-release
  local arch
  arch=$(dpkg --print-architecture)
  case "${ID}_${VERSION_CODENAME:-}_${arch}" in
    debian_trixie_amd64) echo "debian_trixie amd64 6ed957163275088a5ce52b525f82f145014e3289699d576ce26745a4249407f8" ;;
    debian_trixie_arm64) echo "debian_trixie arm64 904615b506c517242f3da3ff2eacd349013fc8cdb2dc205e5e4dad4ec6f62fa9" ;;
    debian_bookworm_amd64) echo "debian_bookworm amd64 5f4bfda43e89178782eac37ae463b1d95d16ac5e45da8b7989ec75404e764993" ;;
    debian_bookworm_arm64) echo "debian_bookworm arm64 b847429cbcf95736fdd5c3be682eea38cae4d689f012eec9c2d5f221b175d1c2" ;;
    ubuntu_noble_amd64) echo "ubuntu_noble amd64 89a5d1fbe9e032b122202be158a2b25f15c97e0a5b8dcf92a5e6835c7b7b2acd" ;;
    ubuntu_noble_arm64) echo "ubuntu_noble arm64 4d680f3d1eb77fb155ae67af7f9fa25d7fbd38a7f69ae146784c7f738e2eed1a" ;;
    ubuntu_jammy_amd64) echo "ubuntu_jammy amd64 a7fb93f4ce3cfc93a3ddd593c23a437d8929791a79e1598ff2901cc7020b3e8f" ;;
    ubuntu_jammy_arm64) echo "ubuntu_jammy arm64 c537cc8be5bb316fdb9144c8296cdc1e111ca735064e66bb208d986dd61e64f6" ;;
    *)
      echo "ERROR: KasmVNC has no package for ${PRETTY_NAME:-this system} ($arch). A browser desktop needs Debian 12 or 13, or Ubuntu 22.04 or 24.04." >&2
      return 11 ;;
  esac
}

if [ "$mode" = browser ]; then
  kasmvnc=$(kasmvnc_package) || exit 11
  read -r kasmvnc_system kasmvnc_arch kasmvnc_sha256 <<< "$kasmvnc"
  echo '==> Installing the XFCE desktop'
  apt_install xfce4 xfce4-terminal dbus-x11 openssl ca-certificates curl
  # A web browser on the desktop, for logins and downloads (Debian's; Ubuntu only has Firefox as a snap).
  if apt-cache show firefox-esr >/dev/null 2>&1; then apt_install firefox-esr; fi
  case "$(dpkg-query -W -f='${Version}' kasmvncserver 2>/dev/null || true)" in
    "$kasmvnc_version"-*) echo "KasmVNC $kasmvnc_version is installed" ;;
    *)
      echo "==> Installing KasmVNC $kasmvnc_version"
      download=$(mktemp -d)
      package="$download/kasmvncserver_${kasmvnc_system}_${kasmvnc_version}_${kasmvnc_arch}.deb"
      curl -fsSL -o "$package" "https://github.com/kasmtech/KasmVNC/releases/download/v$kasmvnc_version/$(basename "$package")"
      echo "$kasmvnc_sha256  $package" | sha256sum -c --quiet - || { echo "ERROR: the KasmVNC download doesn't match its checksum." >&2; exit 12; }
      chmod 755 "$download" && chmod 644 "$package"
      apt_install "$package"
      rm -rf "$download" ;;
  esac

  # KasmVNC's login, which only the Manager uses (it adds it to every request through its tunnel).
  password_file="$HOME/.deveagentmanager/browser-desktop.password"
  if [ ! -s "$password_file" ]; then
    (umask 077; head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32 > "$password_file")
  fi
  printf '%s\n%s\n' "$(cat "$password_file")" "$(cat "$password_file")" | kasmvncpasswd -u "$(id -un)" -w >/dev/null
  printf '{"display": ":1", "port": 6901}\n' > "$HOME/.deveagentmanager/browser-desktop.json"
  chmod 600 "$HOME/.deveagentmanager/browser-desktop.json"

  # Compositing off from the first login on (each login turns it off again: KasmVNC's GPU mode can't use it).
  xfwm4="$HOME/.config/xfce4/xfconf/xfce-perchannel-xml/xfwm4.xml"
  if [ ! -f "$xfwm4" ]; then
    mkdir -p "$(dirname "$xfwm4")"
    printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' '<channel name="xfwm4" version="1.0">' \
      '  <property name="general" type="empty">' '    <property name="use_compositing" type="bool" value="false"/>' \
      '  </property>' '</channel>' > "$xfwm4"
  fi
  mkdir -p "$HOME/.config/systemd/user"
  printf '%s\n' '[Unit]' 'Description=DeveAgentManager browser desktop (XFCE on KasmVNC)' '' '[Service]' \
    'ExecStart=%h/.deveagentmanager/browser-desktop.sh' 'Restart=always' 'RestartSec=3' '' '[Install]' 'WantedBy=default.target' \
    > "$HOME/.config/systemd/user/dam-browser-desktop.service"
else
  echo '==> Configuring desktop autologin'
  sudo -n python3 "$HOME/.deveagentmanager/desktop.py" configure "$(id -un)"
  if getent group autologin >/dev/null; then sudo -n usermod -a -G autologin "$(id -un)"; fi
fi
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
# Refresh only display settings; do not evaluate any session file as shell code. XDG_RUNTIME_DIR stays: it is the
# account's (systemctl --user needs it), with or without a desktop.
if [ -x "$HOME/.deveagentmanager/desktop.py" ]; then
  dam_desktop_exports=$(python3 "$HOME/.deveagentmanager/desktop.py" env 2>/dev/null) || dam_desktop_exports=""
  unset DISPLAY XAUTHORITY WAYLAND_DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_SESSION_TYPE XDG_CURRENT_DESKTOP XDG_SESSION_ID
  if [ -n "$dam_desktop_exports" ]; then eval "$dam_desktop_exports"; fi
  unset dam_desktop_exports
fi
EOF
chmod 600 "$HOME/.deveagentmanager/desktop-env.sh"
python3 "$HOME/.deveagentmanager/desktop.py" remove-instructions

if [ "$mode" = browser ]; then
  # Runs as long as the Runner does (setup enabled lingering), and comes back after a crash or logging out.
  export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
  systemctl --user daemon-reload
  systemctl --user enable dam-browser-desktop.service >/dev/null 2>&1
  if systemctl --user is-active --quiet dam-browser-desktop.service; then
    echo 'Existing desktop left running. Changes apply when the desktop or the Runner restarts.'
  else
    echo '==> Starting the browser desktop'
    systemctl --user start dam-browser-desktop.service
  fi
  for attempt in $(seq 1 30); do
    desktop_status=$(python3 "$HOME/.deveagentmanager/desktop.py" status)
    if python3 -c 'import json,sys;sys.exit(json.load(sys.stdin)["state"] != "ready")' <<< "$desktop_status"; then
      echo "$desktop_status"
      exit 0
    fi
    sleep 2
  done
  echo "$desktop_status"
  systemctl --user status dam-browser-desktop.service --no-pager -l 2>&1 | tail -15 || true
  echo 'ERROR: the browser desktop did not start.' >&2
  exit 13
fi

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
