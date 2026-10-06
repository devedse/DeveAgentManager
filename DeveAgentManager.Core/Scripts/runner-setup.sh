#!/usr/bin/env bash
# DeveAgentManager Runner setup. Runs as the Runner Account over SSH.
# Idempotent: it is safe to run again (Runner > Re-run setup).
set -euo pipefail

DAM_DIR="$HOME/.deveagentmanager"
mkdir -p "$DAM_DIR"
# The git identity came from here before each user had their own.
rm -f "$DAM_DIR/setup-vars.sh"

step() { printf '\n==> %s\n' "$*"; }

if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
elif sudo -n true 2>/dev/null; then
  SUDO="sudo -n"
else
  echo "ERROR: $(whoami) needs passwordless sudo to install packages." >&2
  exit 10
fi

if ! command -v apt-get >/dev/null 2>&1; then
  echo "ERROR: only Debian/Ubuntu based Runners are supported (apt-get not found)." >&2
  exit 11
fi

if command -v cloud-init >/dev/null 2>&1; then
  step "Waiting for cloud-init to finish"
  $SUDO cloud-init status --wait >/dev/null 2>&1 || true
fi

apt_get() {
  $SUDO env DEBIAN_FRONTEND=noninteractive apt-get -q -y \
    -o DPkg::Lock::Timeout=600 \
    -o Dpkg::Options::=--force-confdef \
    -o Dpkg::Options::=--force-confold "$@"
}

step "Installing base packages"
apt_get update
apt_get install --no-install-recommends \
  ca-certificates curl wget gnupg git tmux ncurses-term jq ripgrep fd-find \
  unzip zip xz-utils less nano procps openssh-client locales

node_ok() {
  command -v node >/dev/null 2>&1 &&
    node -e 'const [a,b]=process.versions.node.split(".").map(Number);process.exit(a>22||(a===22&&b>=19)?0:1)'
}

if ! node_ok; then
  step "Installing Node.js 24 LTS (NodeSource)"
  curl -fsSL https://deb.nodesource.com/setup_24.x -o /tmp/nodesource_setup.sh
  $SUDO bash /tmp/nodesource_setup.sh
  rm -f /tmp/nodesource_setup.sh
  apt_get install nodejs
fi
echo "node $(node --version), npm $(npm --version)"

step "Configuring npm for global installs without sudo"
mkdir -p "$HOME/.npm-global"
npm config set prefix "$HOME/.npm-global"

step "Writing shell environment"
cat > "$DAM_DIR/env.sh" <<'EOF'
# Managed by DeveAgentManager. Sourced by login shells, agents and the Manager's commands.
export PATH="$HOME/.npm-global/bin:$HOME/.local/bin:$PATH"
case "${LANG:-}" in *UTF-8*|*utf8*) ;; *) export LANG=C.UTF-8 ;; esac
if [ -f "$HOME/.deveagentmanager/agent-env.sh" ]; then
  . "$HOME/.deveagentmanager/agent-env.sh"
fi
EOF
for rc in "$HOME/.bashrc" "$HOME/.profile"; do
  touch "$rc"
  if ! grep -q 'deveagentmanager/env.sh' "$rc"; then
    printf '\n# DeveAgentManager\n[ -f "$HOME/.deveagentmanager/env.sh" ] && . "$HOME/.deveagentmanager/env.sh"\n' >> "$rc"
  fi
done
touch "$HOME/.hushlogin"
mkdir -p "$HOME/work"

step "Configuring tmux"
tmux_version=$(tmux -V | sed -E 's/^tmux (next-)?([0-9]+)\.([0-9]+).*/\2 \3/')
read -r tmux_major tmux_minor <<< "$tmux_version"
echo "tmux $tmux_major.$tmux_minor"
tmux_block="$(cat <<'EOF'
# >>> DeveAgentManager >>>
set -g default-terminal "tmux-256color"
set -as terminal-features ",xterm-256color:RGB"
set -g extended-keys on
set -g history-limit 100000
set -g mouse on
set -g escape-time 10
set -g focus-events on
set -g status off
set -g set-clipboard on
EOF
)"
if [ "$tmux_major" -gt 3 ] || { [ "$tmux_major" -eq 3 ] && [ "$tmux_minor" -ge 5 ]; }; then
  # Lets pi tell Shift+Enter from Enter (see pi's docs/tmux.md).
  tmux_block="$tmux_block"$'\n'"set -g extended-keys-format csi-u"
fi
tmux_block="$tmux_block"$'\n'"# <<< DeveAgentManager <<<"
touch "$HOME/.tmux.conf"
sed -i '/^# >>> DeveAgentManager >>>$/,/^# <<< DeveAgentManager <<<$/d' "$HOME/.tmux.conf"
printf '%s\n' "$tmux_block" >> "$HOME/.tmux.conf"
if tmux ls >/dev/null 2>&1; then
  tmux source-file "$HOME/.tmux.conf" || true
fi

step "Configuring git"
git config --global init.defaultBranch main
# Everyone's git identity and GitHub token are their own (~/.deveagentmanager/users/<id>/gitconfig, through
# GIT_CONFIG_GLOBAL). The shared settings have neither, so nobody commits or pushes as someone else.
git config --global --unset-all user.name 2>/dev/null || true
git config --global --unset-all user.email 2>/dev/null || true
git config --global --unset-all credential.helper 2>/dev/null || true
rm -f "$HOME/.git-credentials"

# Keep tmux (and so the agents) running when no SSH session is open.
$SUDO loginctl enable-linger "$(whoami)" >/dev/null 2>&1 || true

step "Base setup complete"
