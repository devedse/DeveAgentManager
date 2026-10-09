# Codex login renewal: the Manager renews, Runners don't

Status: **implemented** (2026-10-08), branch `feature/codex-central-auth`. Replaces the reverted cross-Runner sync
(`a569c95`). Background: [internet-facing-plan.md](internet-facing-plan.md#follow-up-investigation-simultaneous-codex-refresh-2026-10-07).

## Problem

A ChatGPT login has a rotating refresh token: each one works once. DAM used to copy the whole login (`auth.json`,
refresh token included) to every Runner. Codex on each Runner renewed on its own, so two Runners could submit the same
refresh token: one won, the other's login died (`refresh_token_reused`). OpenAI's guidance says the same: serialize
refreshes, don't share a copied login between concurrent machines.

## Design

**One renewer per login: the Manager.** Runners get only the access token, which Codex can't renew.

- The Manager stores the full login encrypted (as before). A Runner gets `auth.json` with
  `"auth_mode": "chatgptAuthTokens"` and an empty refresh token (`CodexHarness.RunnerAccountCopy`).
- Codex never renews that mode: it doesn't renew ahead of time, and its renew step does nothing. On a 401 it reloads
  `auth.json` and retries (`codex-rs/login/src/auth/manager.rs`, 0.160.1).
- The Manager renews a login when its access token (about 10 days) has less than 3 days left
  (`CodexHarness.RenewAccountAsync`). It sends the same request as the CLI: JSON `grant_type=refresh_token`, the
  CLI's `client_id` and the refresh token, to `https://auth.openai.com/oauth/token`.
- `AccountService.RenewIfDueAsync` allows one renewal per account at a time. The next caller sees the saved result
  instead of renewing again. The result is saved encrypted before any Runner gets it, and dropped when the account
  was reconnected or removed meanwhile (conditional update on version and stored secret). The request is never
  cancelled by a caller: one cancelled after it was sent may already have used up the refresh token.
- `AccountRenewalService` renews due logins every 10 minutes, then compares each ready Runner's account version files
  and rewrites copies that differ (written next to the file, then renamed). It also deletes folders of removed accounts.
  Agent starts renew through the same lock.
- **Agents already running pick up the change too:** before renewing, Codex reloads `auth.json`. If the file changed
  (DAM pushed it), it uses that file and doesn't renew. So an agent holding an old refresh token in memory switches
  to the pushed copy instead of renewing.

## Evidence

`tools/codex-auth-gate/gate.py` runs the real installed CLI with fake tokens against a local HTTPS mock of ChatGPT.
Any request to the token endpoint fails the gate. With Codex 0.160.1:

1. An access-token-only `auth.json` serves a real model request.
2. Access expired, the Manager pushes a new file during the request: 401, then Codex reloads and retries successfully.
3. Expired and nothing pushed: it fails cleanly, still without a refresh request.
4. An app-server started with a full login (refresh token in memory): after the file is replaced, entering its refresh
   window makes it use the pushed token, with no refresh request.

`DeveAgentManager.Tests/AccountRenewalTests.cs` uses a fake token endpoint where every refresh token works once:

- 6 callers expiring together send one renewal.
- The result is stored encrypted.
- A reconnect during renewal wins.
- A used-up token asks for reconnecting and keeps the stored login; a network error is retried later.
- Older logins and API keys are never renewed by the Manager.

Rerun the gate after upgrading Codex (Runners install `@openai/codex@latest`).

## Existing logins

Logins connected before this keep their old behavior (`Account.ManagedRenewal = false`): their stored copy may already
be used up by a Runner, and trying it could end the login a Runner still uses. The accounts panel says
"Connect it again so the Manager keeps it signed in". Reconnecting starts a fresh login. Within 10 minutes every Runner
gets the new access-only copy, and running agents switch to it as described above.

## Limits

- **One Manager per database.** The lock is in memory.
- **A renewal whose answer is lost** (e.g. timeout after sending) may have used up the refresh token. The next try then
  gets `refresh_token_reused`: the account shows "Connect it again" and agents keep working until their access expires.
  Nothing pretends it worked.
- **A Runner that was off for more than 3 days** may hold expired access. Its agents get 401s until the next pass
  (≤ 10 minutes after it is back); Codex then reloads the pushed file.
- **Access already issued** stays valid until it expires (about 10 days) after a reconnect or removal. Removal deletes
  the files from Runners.
- **Access tokens are still on Runners.** Anyone on a Runner can use one until it expires, but nobody there can renew
  the login.
