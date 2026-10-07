# Internet-facing deployment — plan

Status: **planned, not started**. Last updated 2026-10-07.

Today the Manager is meant for a LAN, a VPN, or behind a reverse proxy with its own login. This plan makes it safe to
expose directly (HTTPS through a plain reverse proxy, no extra login in front). A Portal login is effectively a shell
on every Runner it can reach (agents and shells have sudo), so a stolen or guessed password costs a lot.

| Part | Status |
|---|---|
| Phase 1: fix what is unsafe when exposed | Planned |
| Phase 2: second factor and sessions | Waiting for decisions (section 3) |
| Phase 3: review and checks | Planned |
| Reverse proxy guidance (nginx proxy manager) | Written (section 4) |

## 1. What is already in place

- Every page, API call, live update and terminal needs a sign-in (fallback policy); only the sign-in page, static
  files and `/health` are anonymous.
- Passwords are hashed (ASP.NET Core Identity); changing a password or disabling a user signs them out everywhere
  (security stamp), including open terminals and VS Code.
- Every API endpoint declares who may call it; a test checks this against the full route table. Other people's things
  answer 404.
- Changes through the API need the Portal's header and the same origin; terminal and live-update sockets refuse other
  origins. The Portal can't be framed by other sites.
- Secrets (harness logins, GitHub tokens, Runner passwords) are encrypted at rest and never returned by the API.
- VS Code runs on its own origin with short-lived tickets.

## 2. Phase 1 — fix what is unsafe when exposed

No decisions needed.

1. **Trusted proxies.** `X-Forwarded-For/Proto/Host` are accepted from any client today, so exposed directly a client
   can fake its IP address (defeating per-IP limits and the audit log) and the scheme/host the Manager thinks it is on.
   Trust only proxies on private networks and loopback by default; `TRUSTED_PROXIES` (IPs or CIDRs) for others.
2. **Same-origin check on the account forms.** Sign-in, sign-out and change-password are plain form posts without the
   origin check the API has: another site can sign a visitor in as the attacker (login CSRF) or sign them out. Refuse
   form posts from other origins.
3. **Password guessing.**
   - Rate-limit sign-in per client IP and overall (ASP.NET Core rate limiting).
   - Replace the hard per-user lockout (5 wrong passwords, 5 minutes) with a growing delay, so nobody can lock the admin
     out on purpose.
   - Record the client IP with failed and successful sign-ins in the audit log.
4. **Security headers.** HSTS over HTTPS; `X-Content-Type-Options: nosniff`; `Referrer-Policy: same-origin`;
   `Permissions-Policy` (no camera, microphone, geolocation); a Content-Security-Policy that fits the Blazor
   WebAssembly app and xterm (`script-src 'self' 'wasm-unsafe-eval'`, no inline scripts, `connect-src 'self'`,
   `frame-src` limited to the editor origin). The sign-in cookie becomes `Secure` over HTTPS.
5. **First start.** Without `ADMIN_PASSWORD`, the sign-in page shows the default password and whoever arrives first gets
   in. Instead, print a one-time setup code to the container log; the first sign-in needs it. The hint on the sign-in
   page goes away.
6. **VS Code (port 8081).**
   - Recommend `EDITOR_URL` with `{runner}` (one host per Runner) for internet-facing setups, and warn in the log
     otherwise.
   - Allow `{host}` in `EDITOR_URL` (the Portal's host the browser used), so a Portal reachable on two hostnames gets
     the matching editor host for each, e.g. `https://{runner}.code.{host}`.
   - How VS Code is protected today: the Portal issues a ticket (signed, 2 minutes) for one user and one Runner, only to
     a signed-in user with Use access, and only to the Portal's own pages; it becomes a cookie for that Runner only;
     every request checks again that the user exists, is enabled, has the same security stamp and still has access;
     open connections close when that ends. A second factor (phase 2) covers it automatically.
   - **Single-use tickets** with a shorter lifetime (about 30 seconds): a ticket is in the URL, and URLs end up in
     proxy access logs.
   - **Signing out ends VS Code too.** Today the VS Code cookie stays valid for up to 12 hours after signing out of
     the Portal (signing out doesn't change the security stamp). Tie it to the sign-in session (phase 2 sessions).
   - **Require `{runner}` in `EDITOR_URL` when internet-facing** (or warn): on a shared VS Code host the per-Runner
     cookie path is no boundary, so a compromised Runner's VS Code could use the cookie of another Runner opened in
     the same browser.
   - **Serve VS Code on the Portal's port, by hostname.** A request whose host matches `EDITOR_URL` goes to VS Code;
     everything else to the Portal. Port 8081 and its proxy upstream are then optional (kept for setups without a
     hostname of their own). The Portal's cookie isn't even sent to another hostname, unlike another port today.
   - Not a path on the Portal's host (`/vscode/...`): VS Code runs content from the Runner in the browser (extensions,
     Markdown and notebook previews, webviews), which an agent can write. On the Portal's origin that script could use
     the user's sign-in for anything in the Portal. The separate origin is the security boundary.
7. **Error responses.** Make sure no stack traces or internal details reach anonymous visitors in Production (the
   developer exception page is only on in Development; check the problem-details output).

## 3. Phase 2 — second factor and sessions (decisions needed)

- **Second factor.** Options:
  - Authenticator-app codes (TOTP) with recovery codes: built in, works everywhere. **Suggested first.**
  - Passkeys (WebAuthn): phishing-resistant; more code and browser quirks.
  - Sign-in through an external identity provider (OIDC: Authentik, Keycloak, Google): best when one already exists.

  Also decide whether user managers can require it for everyone, or for users with roles.
- **Sessions.**
  - A list of your signed-in devices (browser, IP, last seen) that you can sign out.
  - Asking for the password (or code) again before sensitive actions: viewing or setting a Runner password, changing
    roles, resetting someone's password, deleting a user.
  - "Remember me" shorter than the current 30 days (suggested: 7 days sliding).
  - VS Code cookies belong to the sign-in session: signing out, or signing a device out, ends them.
- **HTTPS.** Keep it in the reverse proxy (nginx proxy manager, Caddy): less code and certificates kept up to date
  elsewhere. HTTPS inside the Manager is not planned.

## 4. Reverse proxy: nginx proxy manager

For `dam.devedse.click` and `dam.devedse.duckdns.org`, both forwarding to the Manager's port 8080.

**Portal proxy host** (both domain names on one proxy host is fine):

- Scheme `http`, forward to the Manager host, port `8080`.
- **Websockets Support: on.** Terminals, live updates and VS Code use WebSockets; without it they keep reconnecting.
- **Cache Assets: off.** The Manager sets its own cache headers; nginx caching can serve an old app after an update.
- Block Common Exploits: can stay on.
- SSL: Let's Encrypt certificate, Force SSL, HTTP/2, HSTS on. "HSTS Subdomains" also covers the VS Code hosts below,
  which is fine as long as they are all HTTPS.
- Nothing needed under Advanced: nginx proxy manager already sends `Host`, `X-Forwarded-For` and `X-Forwarded-Proto`,
  allows large uploads (session imports) and keeps WebSockets open (the Manager pings every 15–30 seconds).
- Keep the access list (extra login) until phases 1 and 2 are done.

**VS Code proxy host** (port 8081; after phase 1 also port 8080), with a wildcard name so each Runner gets its own host:

- Domain `*.code.dam.devedse.click` (and/or `*.code.dam.devedse.duckdns.org`), forward to the Manager host, port
  `8081` (or `8080` once VS Code is served by hostname), Websockets Support on, Cache Assets off.
- A wildcard certificate needs a Let's Encrypt DNS challenge (nginx proxy manager supports DuckDNS and most DNS
  providers). The DNS name must resolve: DuckDNS resolves every name under `devedse.duckdns.org` already; for
  `devedse.click` add a wildcard record `*.code.dam` pointing to the proxy.
- Compose: `EDITOR_URL=https://{runner}.code.dam.devedse.click`. Until `{host}` exists (phase 1), choose one domain:
  the Portal on the other domain can still use VS Code through **Open in a new tab**. Embedded in the Portal, VS Code
  only works when it is the same site as the Portal (`code.dam.devedse.click` with `dam.devedse.click` is).
- Without a wildcard: one host, e.g. `code.dam.devedse.click` → port 8081, and `EDITOR_URL=https://code.dam.devedse.click`.
  It works, but Runners opened in the same browser can reach each other's VS Code.

**After phase 1:** the proxy's address must be trusted. nginx proxy manager on the same Docker host or LAN (a private
address) is trusted by default; otherwise set `TRUSTED_PROXIES`.

## 5. Phase 3 — review and checks

- **Dependency check in CI:** `dotnet list package --vulnerable --include-transitive` fails the build on known
  vulnerable packages; the base image is rebuilt on every release.
- **Automated scan:** an OWASP ZAP baseline scan against a test instance, signed in and not.
- **Review** of everything that answers without a sign-in: the sign-in page and form, static files, `/health`, the
  editor ticket endpoint, WebSocket upgrades.
- **Tests** for each phase-1 item: forged forwarded headers from an untrusted address, form posts from another origin,
  rate limits, headers present, setup code required.

## 6. Decisions

- 2026-10-07 — HTTPS stays in the reverse proxy.
- 2026-10-07 — VS Code keeps an origin of its own, but can share the Portal's port: it is chosen by hostname.
- Open: second factor (TOTP first?), requiring it, session lifetime, re-authentication for sensitive actions.
