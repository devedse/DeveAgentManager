#!/usr/bin/env bash
# Refresh package lists and simulate the same upgrade as Update all. Never installs or removes a package.
set -euo pipefail
export LC_ALL=C
if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
else
  SUDO="sudo -n"
fi
# Treat a failed repository as an error rather than reporting old lists as a fresh check.
$SUDO env DEBIAN_FRONTEND=noninteractive apt-get -q update \
  -o APT::Update::Error-Mode=any -o Acquire::Retries=2 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 \
  -o DPkg::Lock::Timeout=60 >&2
simulation="$($SUDO env LC_ALL=C apt-get -s dist-upgrade)"
printf '%s\n' "$simulation" | awk '/^Inst / { count++ } END { printf "DAM_PACKAGES=%d\n", count }'
if [ -f /var/run/reboot-required ]; then echo DAM_REBOOT=1; else echo DAM_REBOOT=0; fi
