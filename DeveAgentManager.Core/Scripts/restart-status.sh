# DeveAgentManager: whether the Runner needs a restart, and the kernels, for the update checks (Restart's badge, Update all).
# Prepended to those scripts (RunnerSetupService.RestartStatusScript); defines functions and prints nothing by itself.
# Never installs anything. Kernel releases are as uname -r prints them.

dam_kernel_sort() {
  if command -v linux-version >/dev/null 2>&1; then linux-version sort; else sort -V; fi
}

# Prints DAM_REBOOT (the system's own restart signal) and the kernels: DAM_KERNEL_RUNNING; DAM_KERNEL_INSTALLED, the newest
# installed kernel when newer (a restart loads it, often installed by unattended-upgrades); DAM_KERNEL_AVAILABLE, the
# newest of the releases given as arguments (what dist-upgrade would install) when newer than both. Containers without
# their own kernel packages report neither.
dam_restart_status() {
  local running newest top installed="" available=""
  running="$(uname -r)"
  if command -v linux-version >/dev/null 2>&1; then
    newest="$(linux-version list 2>/dev/null | dam_kernel_sort | tail -n 1 || true)"
  else
    newest="$(ls -1 /boot/vmlinuz-* 2>/dev/null | sed 's|^/boot/vmlinuz-||' | dam_kernel_sort | tail -n 1 || true)"
  fi
  top="$(printf '%s\n' "$running" "$newest" | grep -v '^$' | dam_kernel_sort | tail -n 1 || true)"
  [ "$top" = "$running" ] || installed="$top"
  top="$(printf '%s\n' "$running" "$newest" "$@" | grep -v '^$' | dam_kernel_sort | tail -n 1 || true)"
  [ "$top" = "$running" ] || [ "$top" = "$newest" ] || available="$top"
  if [ -f /var/run/reboot-required ]; then echo DAM_REBOOT=1; else echo DAM_REBOOT=0; fi
  printf 'DAM_KERNEL_RUNNING=%s\nDAM_KERNEL_INSTALLED=%s\nDAM_KERNEL_AVAILABLE=%s\n' "$running" "$installed" "$available"
}
