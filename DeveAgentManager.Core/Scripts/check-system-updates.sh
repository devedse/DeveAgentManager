#!/usr/bin/env bash
# Refresh package lists and simulate the same upgrade as Update all. Never installs or removes a package.
# Runs after restart-status.sh (RunnerSetupService.SystemCheckScript), which defines dam_restart_status.
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
# Keep the same bounded detail limit as RunnerUpdatesDto.MaxSystemPackageDetails.
printf '%s\n' "$simulation" | awk '
  /^Inst / {
    count++
    if (count <= 200) {
      installed=""; latest=$3
      if ($3 ~ /^\[/) { installed=$3; gsub(/^\[|\]$/, "", installed); latest=$4 }
      gsub(/^\(|\)$/, "", latest)
      printf "DAM_PACKAGE\t%s\t%s\t%s\n", $2, installed, latest
    }
  }
  END { printf "DAM_PACKAGES=%d\nDAM_PACKAGE_DETAILS=1\n", count }'
# New kernel images dist-upgrade would install (not debug symbols or unsigned builds), by release name.
kernels="$(printf '%s\n' "$simulation" | awk '$1 == "Inst" && $2 ~ /^linux-image-[0-9]/ && $2 !~ /-(dbg|unsigned)$/ { print substr($2, 13) }')"
# shellcheck disable=SC2086 # One release name per line; release names contain no spaces.
dam_restart_status $kernels
