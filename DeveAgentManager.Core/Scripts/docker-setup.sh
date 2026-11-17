#!/usr/bin/env bash
# DeveAgentManager: Docker on the Runner (part of every setup, after runner-setup.sh). Runs as the Runner Account over
# SSH. Safe to repeat: a Docker that is already there (the machine's own, or from an earlier setup) is left as it is,
# and Update all keeps Docker's packages current like the other system packages.
set -euo pipefail

step() { printf '\n==> %s\n' "$*"; }

if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
elif sudo -n true 2>/dev/null; then
  SUDO="sudo -n"
else
  echo "ERROR: $(whoami) needs passwordless sudo to install Docker." >&2
  exit 10
fi

apt_get() {
  $SUDO env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=l apt-get -q -y \
    -o DPkg::Lock::Timeout=600 \
    -o Dpkg::Options::=--force-confdef \
    -o Dpkg::Options::=--force-confold "$@"
}

# Docker's signing key for its Debian and Ubuntu repositories (https://docs.docker.com/engine/install/debian/).
docker_key_fingerprint=9DC858229FC7DD38854AE2D88D81803C0EBFCD88

# Prints "<distribution> <release>" when Docker's own repository has packages for this system.
docker_repository() {
  . /etc/os-release
  local distro="" release=""
  case "${ID:-}" in
    debian | ubuntu) distro=$ID; release=${VERSION_CODENAME:-} ;;
    *) if [ -n "${UBUNTU_CODENAME:-}" ]; then distro=ubuntu; release=$UBUNTU_CODENAME; fi ;;
  esac
  [ -n "$distro" ] && [ -n "$release" ] || return 1
  curl -fsI -m 20 "https://download.docker.com/linux/$distro/dists/$release/Release" >/dev/null 2>&1 || return 1
  echo "$distro $release"
}

if command -v docker >/dev/null 2>&1; then
  echo "Docker is already installed ($(docker --version 2>/dev/null || echo 'unknown version')): left as it is."
else
  step "Installing Docker"
  # Before Docker's first start. Logs are rotated (Docker's local driver), and containers keep running while Update all
  # upgrades Docker. Everything else, the build cache included, stays at Docker's defaults.
  if ! $SUDO test -e /etc/docker/daemon.json; then
    $SUDO install -m 0755 -d /etc/docker
    printf '%s\n' '{' '  "log-driver": "local",' '  "live-restore": true' '}' | $SUDO tee /etc/docker/daemon.json >/dev/null
  fi
  if repository=$(docker_repository); then
    read -r distro release <<< "$repository"
    key=$(mktemp)
    curl -fsSL -m 60 "https://download.docker.com/linux/$distro/gpg" -o "$key"
    fingerprint=$(gpg --show-keys --with-colons "$key" 2>/dev/null | awk -F: '$1 == "fpr" { print $10; exit }')
    if [ "$fingerprint" != "$docker_key_fingerprint" ]; then
      rm -f "$key"
      echo "ERROR: Docker's signing key has an unexpected fingerprint ($fingerprint)." >&2
      exit 12
    fi
    $SUDO install -m 0755 -d /etc/apt/keyrings
    $SUDO install -m 0644 "$key" /etc/apt/keyrings/docker.asc
    rm -f "$key"
    printf '%s\n' 'Types: deb' "URIs: https://download.docker.com/linux/$distro" "Suites: $release" 'Components: stable' \
      "Architectures: $(dpkg --print-architecture)" 'Signed-By: /etc/apt/keyrings/docker.asc' \
      | $SUDO tee /etc/apt/sources.list.d/docker.sources >/dev/null
    apt_get update
    apt_get install --no-install-recommends docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  else
    echo "Docker's repository has no packages for this system: installing the distribution's Docker."
    packages=(docker.io)
    for extra in docker-compose docker-buildx; do
      if apt-cache show "$extra" >/dev/null 2>&1; then packages+=("$extra"); fi
    done
    apt_get install --no-install-recommends "${packages[@]}"
  fi
fi

# The Runner Account uses Docker without sudo (no more than it may already: it has passwordless sudo).
if getent group docker >/dev/null && ! id -nG "$(whoami)" | tr ' ' '\n' | grep -qx docker; then
  $SUDO usermod -aG docker "$(whoami)"
  echo "$(whoami) is now in the docker group."
  # Programs keep the groups they started with. A running tmux server starts every new agent too, and a desktop starts
  # its apps: with either running, agents and the desktop get Docker once the Runner restarts (Restart's badge on the
  # Runner page says so). Without them (a new Runner), only the account's idle systemd manager is restarted, so the
  # browser desktop it starts later has Docker as well.
  export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
  graphical=$(loginctl list-sessions --no-legend 2>/dev/null | awk -v user="$(whoami)" '$3 == user { print $1 }' \
    | while read -r session; do loginctl show-session "$session" -p Type --value 2>/dev/null; done | grep -cE '^(x11|wayland)$' || true)
  if tmux ls >/dev/null 2>&1 || [ "${graphical:-0}" -gt 0 ] || systemctl --user is-active --quiet dam-browser-desktop.service 2>/dev/null; then
    $SUDO sh -c 'touch /var/run/reboot-required && echo docker >> /var/run/reboot-required.pkgs'
    echo "Restart the Runner so running agents, the agents started next and the desktop can use Docker."
  else
    $SUDO systemctl try-restart "user@$(id -u).service" >/dev/null 2>&1 || true
  fi
fi

$SUDO systemctl enable --now docker >/dev/null 2>&1 || true
if $SUDO docker info >/dev/null 2>&1; then
  $SUDO docker info --format 'Docker {{.ServerVersion}} is running (storage {{.Driver}}).'
else
  # A container Runner needs Proxmox's nesting feature for Docker; setup itself doesn't depend on Docker.
  echo "WARNING: Docker is installed but isn't running:"
  $SUDO systemctl status docker --no-pager -l 2>&1 | tail -n 5 || true
  if [ "$(systemd-detect-virt -c 2>/dev/null || true)" = lxc ]; then
    echo "In a Proxmox container, Docker needs the nesting feature (Options > Features > Nesting), then a restart."
  fi
fi
