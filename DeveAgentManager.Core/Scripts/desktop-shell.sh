# BASH_ENV: Bash reads this before noninteractive commands, including bash -lc.
# Preserve a user's existing hook, with a guard if it also sources the Manager environment.
if [ "${DAM_SOURCING_BASH_ENV:-}" != 1 ]; then
  export DAM_SOURCING_BASH_ENV=1
  if [ -n "${DAM_PREVIOUS_BASH_ENV:-}" ] && [ -f "$DAM_PREVIOUS_BASH_ENV" ] && [ "$DAM_PREVIOUS_BASH_ENV" != "$HOME/.deveagentmanager/desktop-shell.sh" ]; then
    . "$DAM_PREVIOUS_BASH_ENV"
  fi
  [ ! -f "$HOME/.deveagentmanager/desktop-env.sh" ] || . "$HOME/.deveagentmanager/desktop-env.sh"
  export BASH_ENV="$HOME/.deveagentmanager/desktop-shell.sh"
  unset DAM_SOURCING_BASH_ENV
fi
