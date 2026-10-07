# Internet-facing deployment — implementation and validation

Updated 2026-10-07. Branch: `feature/internet-facing`. PR: https://github.com/devedse/DeveAgentManager/pull/3.

The user authorized the complete implementation on this branch. Public HTTPS terminates at a plain reverse proxy,
with **no Basic authentication or other proxy login**. DAM performs authentication and resource authorization.
Local direct IP HTTP access remains convenient, with explicitly reduced editor isolation. The shared Runner Linux
account remains a trust boundary: Runner members and agents can read files and use sudo on that Runner. A Portal
account is therefore powerful; browser origin separation does not isolate Linux processes on a shared Runner.

## Status

| Part | Implementation | Validation |
|---|---|---|
| Explicit public origins and trusted proxy peers | Implemented | Host, forwarded scheme/IP and local-exception regression tests |
| Same backend port with separate Runner editor hosts | Implemented | Host/Runner mismatch, two users/two Runners, alternate-origin ticket tests |
| Single-use session-bound editor tickets | Implemented | Concurrent exchange, expiry, logout; fragments exclude tickets from request logs |
| Credential stripping at Runner proxy | Implemented | Fake upstream sees only code-server's cookie; incoming authentication headers removed |
| Account antiforgery and Origin checks | Implemented | Missing/null, foreign and same-site editor Origin rejected; real browser form checks |
| Public cookies and CSP | Implemented | Production published app booted in Chromium behind an isolated HTTPS proxy |
| Durable sign-in sessions and live revocation | Implemented | Copied cookie rejected after logout; connection revocation and expiry tests |
| TOTP, recovery, enforcement and recent authentication | Implemented | RFC vector, concurrent replay/recovery tests; enrollment safeguards |
| Bounded login throttles | Implemented | Concurrency/account regression tests; existing sessions unaffected |
| First-run setup and operator recovery | Implemented | Existing user/access tests; startup code replaces known default, chosen passwords preserved |
| Dependency failure policy | Implemented | NuGet/npm passed locally; initial container/Trivy CI scan passed |
| Isolated browser attack checks | Implemented and run | Hostile editor cannot read Portal, log it out or plant its auth cookie |
| Anonymous/authenticated ZAP baseline | Script and CI configured | Initial CI baselines completed; warnings assessed below. Docker and Java/ZAP unavailable locally |

This table records implementation evidence, **not certification of a live public deployment**. Real NPM/DNS,
certificates and Runner code-server instances have not been changed or exercised by this work. CI and external scans
must be assessed separately; passive scans are never proof of authorization. Current test counts and results belong
in the PR description; don't infer them from the table or an image build number.

## 1. Public and local deployment boundaries

Server settings are shown under **Account menu → Account security** for user managers and configured in compose:

| Setting | Example / default |
|---|---|
| `PUBLIC_ORIGINS` | `https://dam.devedse.click;https://dam.devedse.duckdns.org` |
| `EDITOR_URL` | `https://{runner}.code.{host}`, or flat `https://dam-vscode-{runner}.devedse.click` |
| `TRUSTED_PROXIES` | Exact NPM socket IPs separated by semicolons; empty trusts nobody |
| `ALLOW_LOCAL_ACCESS` | `true` |
| `PROXY_IDENTITY_HEADERS` | Additional header names separated by semicolons |
| `EDITOR_PORT` | `8081`, optional for direct local access |

Public origins must be HTTPS hostnames with no path, credentials, query or fragment. Public configuration requires
`{runner}` in the editor template and a different editor hostname. `{host}` is derived exclusively from an allowed
Portal origin; request-supplied or forwarded hostnames never become arbitrary ticket destinations.

Only explicit proxy peers may supply forwarded client IP and scheme, with one forwarding hop. `X-Forwarded-Host` is
ignored; NPM preserves the requested Host. Unknown public origins receive 421. Unknown Runner editor hosts receive
404; editor hosts serve only editor endpoints, never the Portal, API, login page or hub. The Runner ID in an editor
URL must match its hostname. Host routing works on port 8080, including WebSocket upgrades.

A direct private/loopback client using a private/loopback IP Host (or the explicit local editor template on its editor
port) can use local mode. Trusted proxy connections and any requests carrying forwarding headers cannot activate it,
even if the proxy or forwarded Host is private. This retains the local workflow without letting public proxy traffic
become local by spoofing headers. Public and local authentication use distinct cookie schemes and sessions record
which boundary issued them. Public requests cannot use local credentials.

Keep backend ports off the internet and trust only intended proxy peers. A Runner or unrelated private container must
not belong to the proxy trust list. NPM must overwrite forwarded headers and supply the actual client IP and scheme.

## 2. Why editors keep separate origins

Browsers isolate origins (scheme + hostname + port), not paths. A script served by a Runner on the Portal origin could
call Portal APIs, read responses and perform actions as whoever opened it. Runner content can be modified by members,
agents, extensions or compromised dependencies. An HttpOnly cookie cannot be read, but the browser still sends it.
Cookie Path and CSP are not substitutes for origin separation. **Public VS Code never runs on a Portal path.**

Each Runner therefore has its own editor hostname, e.g. `runner-01.code.dam.devedse.click`. A compromised Runner cannot
read the Portal or another Runner's editor responses. Same site is not same origin: sibling subdomains can still
trigger requests with cookies, so API mutations require the Portal header and matching Origin, sockets check Origin,
account forms require antiforgery tokens and strict Origin, and editor links require same-origin/direct navigation
Fetch Metadata. Public requests without the expected Fetch Metadata do not receive tickets.

Direct local IP access can use a separate editor port. Cookies do not isolate ports, and a shared editor origin does
not isolate multiple Runners; these are the reduced local protections the user explicitly accepted. Local mode still
requires sign-in and Runner authorization.

## 3. Editor authentication and credentials

Opening VS Code requires a usable Portal sign-in and **Use access to the Runner**. Opening it from an agent also
requires read-write agent access; sharing an agent alone does not grant a Runner shell.

Tickets are random, durable, single-use and valid for 30 seconds. Each is bound to a Runner, exact destination origin
and revocable Portal sign-in session. An atomic database delete permits precisely one exchange, including concurrent
requests. The ticket travels in the redirect URL **fragment**, not the query string: fragments never reach reverse
proxy or application access logs. An external bootstrap script clears the fragment before exchanging it through a
same-origin POST. The body is not logged. Editor mutations and WebSockets independently reject foreign Origins. Failed or expired links ask the user to open VS Code from the Portal again.

Public editor cookies use `__Host-DAM.Editor`, Secure, HttpOnly, SameSite=Lax and Path=/ with no Domain. Every request
checks the session, user/security stamp, Runner identity and current Use access. Cookie lifetime cannot override a
revoked or expired sign-in session. Logout ends Portal and editor access without asking for another password merely
to open VS Code. Agents keep running.

The proxy removes browser cookies, Authorization, Proxy-Authorization, forwarded headers, common authentication/
identity headers and configured extra identity headers. Only DAM's own code-server session cookie is injected.
Runner Set-Cookie and Clear-Site-Data never reach the browser; service workers are limited to that editor path.
No proxy Basic auth is planned, but incoming credentials remain isolated if an operator temporarily enables it.

## 4. Account forms, sessions and second factor

Login, logout, password-change and account-security forms include ASP.NET antiforgery tokens. Missing, null,
malformed, foreign and same-site editor Origins are rejected. Logout uses a server-rendered confirmation form so the
WebAssembly client does not invent or bypass antiforgery tokens. Public Portal cookies use `__Host-DAM.Auth`, Secure,
HttpOnly, SameSite=Lax, Path=/ and no Domain. Browser rules prevent editor subdomains planting these cookie names for
the parent domain. Local HTTP credentials have different names and scheme protection.

Sign-in sessions are stored by the Manager. Remembered sessions have **seven days of inactivity** and a **thirty-day
absolute maximum**; non-remembered cookies are discarded at browser shutdown. Session activity is touched at most once
per minute. A user has at most twenty retained active sessions, and sixty-four pending editor tickets. The device list shows browser, client IP and last
activity and supports revocation. Logout and explicit revocation close registered live connections; expiry and other
changes are checked periodically every fifteen seconds as well as per HTTP request. User security-stamp changes end
old credentials. Gaining a role that requires an authenticator closes unenrolled live connections and blocks editor
requests until enrollment; editor dispatch enforces this independently of Portal middleware. Revocation never stops the agent process on a Runner.

TOTP uses RFC 6238 SHA1, six digits, thirty-second intervals and one interval of clock skew. Secrets are encrypted at
rest. Enrollment requires recent authentication and a valid first code. Ten random recovery codes are shown once in a
POST response, hashed at rest and atomically consumed. TOTP counters are atomically advanced to prevent concurrent
replay. Enabling or disabling an authenticator revokes other sessions; disabling requires a current code and cannot
violate enforcement.

A user manager can choose optional, users-with-roles or everyone enforcement. Activation is refused until all affected
enabled users have enrolled. An unenrolled user manager cannot relax the policy to bypass their own enrollment.
New affected users must change their initial password, then enroll before accessing the Portal. Enrollment/recovery
pages and static assets remain available during this flow. There is no passkey or external
identity-provider implementation in this PR.

Viewing/changing a Runner password and mutating users (including roles, resets and deletion) require authentication
within ten minutes. **Account security → Confirm identity** checks the password and, when enrolled, a current
TOTP/recovery code. Account-security changes also require this recent confirmation. A refusal explains where to
confirm; return to the action afterward.

## 5. Guessing protection, first start and recovery

Login, identity confirmation, password changes and authenticator confirmation/removal share fixed five-minute windows:
**20 attempts/account, 30/IP, 200 globally**, with at
most **four concurrent password checks** and at most **4096 limiter entries**. Counters are coordinated under one
lock, expired entries are removed and exceeding limits returns 429 rather than sleeping. These are per Manager
process; scaling to multiple active instances would need a shared limiter and coordinated session event delivery.
This deployment uses a single Manager. Existing sessions continue working during guessing attacks. There is no fixed
account lockout or promise that distributed attackers cannot interfere with legitimate new sign-ins. Completed sign-ins and
failures are audited with the client IP; password-only verification does not update the displayed last sign-in time; failed password checks also retain the existing user-service audit details.

On first start without ADMIN_PASSWORD, a random setup code is printed in the operator's startup log and used as the
initial password; the UI never reveals it. It must be replaced after sign-in. Existing chosen passwords are never
reset at normal startup. A legacy bootstrap account still using known `change-me` with mandatory password change gets
a fresh logged setup code instead. ADMIN_USERNAME/ADMIN_PASSWORD otherwise apply only to initial setup.

Operator recovery is explicit: `RESET_ADMIN_PASSWORD=true` restores the named admin, resets its password (random
logged recovery code if unspecified), removes its authenticator, relaxes enforcement to optional and requires password
change. Old security stamps stop working. Remove the flag immediately after recovery, enroll again and restore the
policy. Protect startup logs and `/data`. This recovery deliberately changes security settings only when an operator
requests it; normal restarts do not reset existing accounts.

## 6. Headers and production errors

The Portal sends HSTS for HTTPS, nosniff, same-origin Referrer/Opener/Resource policies, restrictive Permissions-Policy and CSP with
self-only scripts/connections, wasm-unsafe-eval, per-response nonces for the theme boot script/import map, and editor
frames derived from configured origins. Inline styles remain allowed for MudBlazor/xterm. The Portal cannot be framed
by other origins. Image responses retain their stricter sandbox CSP. Editor responses keep their own policies and
no-referrer; Portal CSP is not imposed on Runner content.

Production page errors show a generic message/request ID. Unexpected API failures return a generic problem, with
details confined to operator logs; editor tunnel errors do not reveal internal exception messages.

## 7. Nginx Proxy Manager: configure once

For `dam.devedse.click` and `dam.devedse.duckdns.org`, HTTPS remains in NPM; internal TLS is not required.

**Portal proxy host** (both names can share one entry): forward HTTP to Manager port **8080**, WebSockets on, asset
caching off, Force SSL on, valid certificate, HSTS on. HSTS subdomains should only be enabled when those subdomains
are served over HTTPS. Final configuration has no proxy access-list login. Preserve Host and supply forwarded HTTPS
and real client IP; configure NPM's actual socket address in TRUSTED_PROXIES.

**One wildcard editor entry per public editor domain, never an entry per Runner:**

| Configure once | Example |
|---|---|
| Wildcard DNS | `*.code.dam.devedse.click` → public NPM address |
| NPM proxy hostname | `*.code.dam.devedse.click` |
| Destination | Manager's address, HTTP, port **8080** |
| WebSockets / asset cache | On / off |
| Certificate | `*.code.dam.devedse.click` via DNS challenge |
| DAM editor template | `https://{runner}.code.{host}` |

`runner-01.code.dam.devedse.click`, `runner-02.code.dam.devedse.click` and every future Runner use the same entry.
Adding Runners needs no NPM, DNS or certificate changes. NPM routes; DAM checks identity and authorization. The wildcard
certificate covers one Runner-label level, not deeper names or bare `code.dam.devedse.click`.

**Behind Cloudflare: flat editor names.** Cloudflare's free certificate covers `*.devedse.click` (one level), not
`runner-01.code.dam.devedse.click`, and a DNS wildcard doesn't apply below the existing `dam.devedse.click` record.
So `EDITOR_URL=https://dam-vscode-{runner}.devedse.click`: the existing `*.devedse.click` DNS record and tunnel route
already reach NPM (verified: `dam-vscode-runner-01.devedse.click` resolves with a valid certificate). NPM needs only one
line in the Portal proxy host's Advanced tab: `server_name ~^dam-vscode-[a-z0-9-]+\.devedse\.click$;`. DAM accepts
`{runner}` anywhere in the first label, so Runners stay one origin each and one name each never spans labels. From the
DuckDNS Portal these editors are cross-site: VS Code opens there with **Open in a new tab**.

Configure a second wildcard entry/certificate once if editors are wanted under the DuckDNS domain as well. Verify
wildcard DNS/certificate support on the chosen provider; this implementation has not changed live DNS. Local optional
8081 mappings can remain on intended LAN routes or be removed. Backend ports must not be publicly reachable.

## 8. Checks and remaining external validation

- Targeted HTTP tests: exact hosts, proxy spoofing, local/public separation, two users/two Runners, missing/null and
  same-site Origins, atomic ticket replay, destination binding, session revocation/expiry, credential stripping,
  TOTP vector/replay/recovery, bounded throttling and the full existing authorization regression suite.
- Published production browser test: `tools/security-browser/check.mjs` boots an isolated Manager and HTTPS proxy,
  loads Blazor under CSP, submits legitimate forms, checks hardened cookies and runs a hostile sibling-origin page.
  The hostile page is an explicit fixture, not a claim that live code-server or every extension has been audited.
- NuGet audit fails on high/critical transitive advisories and audit-source failures through Directory.Build.props.
  npm lockfiles audit vendored addons and browser-test tooling; the addon bytes are verified against their packages.
  Browser dependencies embedded inside upstream NuGet bundles depend on upstream inventory/advisories and still need
  review. Container CI uses Trivy for high/critical OS/application advisories, including unfixed vulnerabilities.
- `tools/security-browser/zap.sh` builds an isolated container and records anonymous/authenticated passive baseline
  reports. Authentication cookies are in temporary test files, never command arguments or logs. Warnings are reported;
  explicit scanner failures/errors fail CI. Passive scanning does not prove authorization or Runner isolation.
- Initial CI completed dependency, container/Trivy, build/test, browser and both passive ZAP checks successfully.
  Passive reports flagged the intentional inline-style CSP allowance (Medium), missing cross-origin headers (Low),
  timestamps in assets (Low) and informational items. Opener/resource policies were added after review. COEP remains
  unset because embedding restrictions would require changes to authenticated cross-origin editor frames. Inline
  styles are needed by the current MudBlazor/xterm app. Framework/static timestamps and comments do not disclose
  credentials; return-URL attributes are Razor-encoded and redirect targets restricted to local paths. Assess the
  final-head CI separately; these reports are supporting evidence, not authorization proof.
- Docker and Java/ZAP are unavailable in the implementation environment. Container/scan jobs are configured but have
  not been claimed as locally passed. Real NPM forwarding, wildcard certificates, production uploads/WebSockets,
  code-server extensions and public DNS must be verified on the operator's deployment before removing external access
  restrictions. No live deployment or master changes are authorized in this implementation worktree.

## Decisions

- Public HTTPS terminates at NPM; final proxy has no additional login.
- Public editors have one hostname per Runner and share the Portal's backend port through hostname dispatch.
- Each public editor domain uses one wildcard proxy/DNS/certificate setup for all Runners.
- Runner Use access is mandatory for VS Code; an agent share alone cannot grant a shell.
- Exact public origins and trusted proxy peers are explicit configuration; public requests cannot activate local mode.
- Local direct IP HTTP remains supported with separate credentials and accepted reduced editor isolation.
- Durable revocable sessions, TOTP/recovery, enrollment safeguards and recent authentication are in this PR.
- Initial defaults: seven-day inactivity, thirty-day absolute session limit, ten-minute recent-authentication window;
  bounded guessing thresholds as documented above. Passkeys/OIDC and multi-instance deployment are outside this scope.
