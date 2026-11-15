#!/usr/bin/env bash
# The browser desktop (systemd --user service dam-browser-desktop): XFCE on KasmVNC, display :1, on the Runner's
# loopback only (the Manager reaches it through an SSH tunnel). KasmVNC's configuration is written at every start, so a
# GPU passed in later is used from the next start on.
set -euo pipefail
display=:1
port=6901
vnc="$HOME/.vnc"
mkdir -p "$vnc"

# KasmVNC's DRI3 mode renders OpenGL and Vulkan apps on the GPU's render node; without one they render in software.
render=""
for node in /dev/dri/renderD*; do
  if [ -c "$node" ] && [ -r "$node" ] && [ -w "$node" ]; then render=$node; break; fi
done
if [ -n "$render" ]; then hw3d=true; else hw3d=false; render=/dev/dri/renderD128; fi

# KasmVNC wants a certificate it can read even without TLS (the Manager's tunnel is the connection's protection). The
# system's own key needs the ssl-cert group, so the desktop has one of its own.
if [ ! -s "$vnc/desktop-key.pem" ] || [ ! -s "$vnc/desktop-cert.pem" ]; then
  (umask 077; openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj /CN=localhost \
    -keyout "$vnc/desktop-key.pem" -out "$vnc/desktop-cert.pem" 2>/dev/null)
fi

cat > "$vnc/kasmvnc.yaml" <<EOF
# Managed by DeveAgentManager: browser-desktop.sh writes this at every start of the desktop.
desktop:
  resolution:
    width: 1920
    height: 1080
  allow_resize: true
  gpu:
    hw3d: $hw3d
    drinode: $render
network:
  protocol: http
  interface: 127.0.0.1
  websocket_port: $port
  use_ipv6: false
  ssl:
    require_ssl: false
    pem_certificate: $vnc/desktop-cert.pem
    pem_key: $vnc/desktop-key.pem
user_session:
  idle_timeout: never
command_line:
  prompt: false
EOF

# Only one desktop on :1: an earlier one (or one left by a crash) is stopped first.
vncserver -kill "$display" >/dev/null 2>&1 || true
if [ -O /tmp/.X1-lock ] && ! pgrep -u "$(id -u)" -x Xvnc >/dev/null; then rm -f /tmp/.X1-lock /tmp/.X11-unix/X1; fi
exec vncserver "$display" -fg -xstartup "$HOME/.deveagentmanager/browser-desktop-session.sh"
