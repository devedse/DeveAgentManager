#!/usr/bin/env bash
# DeveAgentManager: updates the Runner's Debian/Ubuntu packages (Update all, Re-run setup and Update system packages).
# Runs as the Runner Account over SSH. Safe to repeat.
set -euo pipefail

step() { printf '\n==> %s\n' "$*"; }

if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
elif sudo -n true 2>/dev/null; then
  SUDO="sudo -n"
else
  echo "ERROR: $(whoami) needs passwordless sudo to update packages." >&2
  exit 10
fi

# Packages restart their own services; needrestart only lists what else still runs old code, so agents keep running.
apt_get() {
  $SUDO env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=l apt-get -q -y \
    -o DPkg::Lock::Timeout=600 \
    -o Dpkg::Options::=--force-confdef \
    -o Dpkg::Options::=--force-confold "$@"
}

step "Reading the package lists"
apt_get update
step "Upgrading the system packages"
apt_get dist-upgrade

if [ -f /var/run/reboot-required ]; then
  packages="$(sort -u /var/run/reboot-required.pkgs 2>/dev/null | paste -sd ' ' - || true)"
  echo "Restart the Runner to finish the update${packages:+ (for $packages)}."
fi
