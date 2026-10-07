# Internet-facing deployment — plan

Status: **planned, not started**. Last updated 2026-10-07.

Today the Manager is meant for a LAN, a VPN, or behind a reverse proxy with its own login. This plan makes it safe to
expose directly: HTTPS through a plain reverse proxy, no extra login in front. A Portal login is effectively a shell
on every Runner it can reach (agents and shells have sudo), so a stolen or guessed password costs a lot.

Deployment preferences confirmed during review: local access by IP should stay simple, including HTTP without
public certificates or wildcard DNS. The user accepts reduced isolation for that local workflow. This does not
request removing the Portal login locally. The final public setup uses Nginx Proxy Manager for HTTPS and routing,
with **no Basic authentication or other extra login at the proxy**. Public traffic must not inherit local exceptions
merely because it arrives from the proxy's private IP or supplies a private Host/forwarded header.

| Part | Status |
|---|---|
| Phase 1: fix what is unsafe when exposed (section 3) | Planned; VS Code by hostname first |
| Phase 2: second factor and sessions (section 4) | Waiting for decisions |
| Phase 3: review and checks (section 6) | Planned |
| Reverse proxy guidance for nginx proxy manager (section 5) | Written |

## 1. What is already in place

- Every page, API call, live update and terminal needs a sign-in (fallback policy). Only the sign-in page, static
  files and `/health` are anonymous.
- Passwords are hashed (ASP.NET Core Identity). Changing a password or disabling a user signs them out everywhere
  (security stamp), including open terminals and VS Code.
- Every API endpoint declares who may call it; a test checks this against the full route table. Other people's things
  answer 404.
- Changes through the API need the Portal's header and the same origin; terminal and live-update sockets refuse other
  origins. The Portal can't be framed by other sites.
- Secrets are encrypted at rest. Harness credentials and GitHub tokens are not returned by settings APIs; Runner
  passwords are deliberately available through the password-reveal endpoint to authorized Runner managers.
- **VS Code** runs on an origin of its own (section 2), and access to it is checked like the Portal's:
  - The Portal's VS Code link (`/editor/runners/{id}`, or `/editor/agents/{id}` with read-write access to the agent)
    needs the Portal sign-in and **Use access to that Runner**, and only answers the Portal's own pages
    (`Sec-Fetch-Site`), so other sites and VS Code pages can't get tickets.
  - It sends the browser to the VS Code host with a ticket for one user and one Runner: encrypted and signed by the
    Manager, with the user's security stamp, valid for 2 minutes.
  - The ticket becomes a cookie for that one Runner (and, with `{runner}` in `EDITOR_URL`, only on that Runner's own
    hostname).
  - **Every request** checks again: the right Runner, the user exists and is enabled, the security stamp is unchanged,
    and they still have Use access now. Open VS Code connections close as soon as that ends.
  - The Portal's cookie is removed before anything goes to the Runner; code-server's own password stays in the
    Manager; cookies code-server sets never reach the browser.
  - VS Code needs **Runner** access: it has a terminal, so it is a shell on the Runner. Read-write access to a shared
    agent alone isn't enough.
  - Tickets are only issued to a signed-in Portal session, so a second factor (phase 2) covers VS Code automatically.

## 2. Why VS Code needs a hostname of its own

Browsers isolate **origins** (scheme + host + port), not paths. Every script on an origin can call that origin's API
with the user's cookies (an HttpOnly cookie can't be read, but it is sent), set any header, read the answers, and reach
into other pages of that origin, including the Portal page that embeds VS Code. A cookie's `Path` is no security
boundary (RFC 6265, section 8.5).

What VS Code runs in the browser comes from the Runner: code-server is a program there, and whoever can write to the
Runner decides what it serves: everyone with access (one shared Linux account), every agent (with sudo, and possibly
prompt-injected by a repository it cloned), extensions with webviews, packages with install scripts.

- **On a path of the Portal's host** (`/vscode/...`) such a script would run as the Portal: list Runners, read their
  passwords, create and share agents and, for an admin, create users or reset passwords, all as the user who opened
  VS Code and looking legitimate in the audit log. A Content-Security-Policy can't prevent it: the script arrives in
  VS Code's own responses. One compromised Runner would compromise the Portal account of everyone who opens VS Code
  there. **Never on a path.**
- **On another hostname** the same script can't read Portal responses (no CORS), can't send the Portal's custom header
  (the preflight is refused), has the wrong Origin for the API and sockets, can't touch the Portal's page, and its own
  requests don't carry the Portal's cookie. The damage stays on that Runner.
- **Same site is not same origin.** `code.dam.devedse.click` is the same *site* as `dam.devedse.click`, so the browser
  does send the Portal's `SameSite=Lax` cookie with requests a VS Code page makes to the Portal. That is why the API
  also needs its header and Origin, the VS Code links check `Sec-Fetch-Site`, and the account forms need the origin
  check (phase 1).
- **One hostname per Runner** (`{runner}`): on one shared VS Code host, Runner 1's compromised VS Code could make
  requests to `/runners/{runner 2}/code/...`, and the browser would attach Runner 2's cookie (a terminal there). Only
  separate hostnames keep Runners apart in the browser.

## 3. Phase 1 — fix what is unsafe when exposed

The implementation order and the proposals marked below are under review; they have not been implemented.

1. **VS Code by hostname.**
   - Serve VS Code on the Portal's port, chosen by hostname. **Review proposal:** configure the public Portal origins
     explicitly, derive editor hosts only from them, and reject unknown hosts. Never fall through from an unknown
     editor host to the Portal. Retain an explicitly separated local IP access path. Port 8081 becomes optional
     (kept for local setups without a hostname of their own). The
     Portal's cookie isn't even sent to another hostname, unlike another port of the same host today.
   - Allow `{host}` in `EDITOR_URL` (the Portal's host the browser used), so a Portal reachable on two hostnames gets
     the matching VS Code host for each, e.g. `https://{runner}.code.{host}`.
   - **Review proposal:** require `{runner}` in public mode; a warning alone does not isolate Runner origins.
   - **Single-use tickets**, valid about 30 seconds: consume atomically, bind to the intended editor origin and
     sign-in session, and prevent tickets appearing in proxy/application access logs. A short expiry still allows
     someone who sees an unused ticket to race the real browser.
   - **Accepted:** strip incoming `Authorization`, `Proxy-Authorization` and configured proxy identity headers before
     forwarding to a Runner, as well as browser cookies. Supply only the Manager's code-server credential. No proxy
     Basic authentication is planned, but credential isolation should also hold when it is temporarily enabled.
     Test this without breaking code-server's normal authentication and WebSockets.
2. **Trusted proxies.** `X-Forwarded-For/Proto/Host` are accepted from any client today, so exposed directly a client
   can fake its IP address (defeating per-IP limits and the audit log) and the scheme/host the Manager thinks it is on.
   **Review proposal:** trust explicitly configured proxy addresses or narrowly controlled proxy networks, rather
   than all private networks. Local convenience must not let a Runner or unrelated container forge proxy headers.
   Keep public traffic on the HTTPS proxy; backend ports remain accessible only through intended LAN/proxy routes.
3. **Same-origin check on the account forms.** Sign-in, sign-out and change-password are plain form posts without the
   origin check the API has: another site, or a VS Code page (same site), can sign a visitor in as the attacker (login
   CSRF) or sign them out. **Review proposal:** use framework antiforgery tokens plus Origin validation; define
   handling of absent and `null` Origins instead of copying the API helper, which currently accepts missing Origin.
   Tests must include same-site editor subdomains, not just unrelated attacker sites.
4. **Password guessing.**
   - Coordinate limits per client IP, account and overall (ASP.NET Core rate limiting), with concurrency limits
     before password hashing and bounded limiter storage. Parallel requests must not bypass the limits.
   - Replace the blanket growing-delay proposal with a bounded policy tested for guessing and deliberate denial of
     sign-in. A growing delay can still lock out the account owner. Keep existing signed-in sessions usable and
     provide an operator recovery path; exact thresholds remain to be chosen and tested.
   - Record the client IP with failed and successful sign-ins in the audit log.
5. **Security headers.** HSTS over HTTPS; `X-Content-Type-Options: nosniff`; `Referrer-Policy: same-origin`;
   `Permissions-Policy` (no camera, microphone, geolocation); a Content-Security-Policy that fits the Blazor
   WebAssembly app and xterm (`script-src 'self' 'wasm-unsafe-eval'`, no inline scripts, `connect-src 'self'`,
   `frame-src` limited to the VS Code hosts). The sign-in cookie becomes `Secure` over HTTPS.
   **Review proposal:** public authentication cookies use `__Host-`, `Secure`, `HttpOnly`, `Path=/` and no Domain,
   preventing an editor subdomain from planting a parent-domain cookie with the same name. Separate per-Runner
   hosts allow the editor cookie to use `Path=/`. Local HTTP uses distinct cookies that public endpoints do not
   accept. Validate CSP against the actual app (including inline boot code/import maps), not only a header test.
6. **First start.** Without `ADMIN_PASSWORD`, the sign-in page shows the default password and whoever arrives first gets
   in. Instead, print a one-time setup code to the container log; the first sign-in needs it. The hint on the sign-in
   page goes away.
7. **Error responses.** No stack traces or internal details for anonymous visitors in Production (the developer
   exception page is only on in Development; check the problem-details output).

## 4. Phase 2 — second factor and sessions (decisions needed)

- **Second factor.** Options:
  - Authenticator-app codes (TOTP) with recovery codes: built in, works everywhere. **Suggested first.**
  - Passkeys (WebAuthn): phishing-resistant; more code and browser quirks.
  - Sign-in through an external identity provider (OIDC: Authentik, Keycloak, Google): best when one already exists.

  Also decide whether user managers can require it for everyone, or for users with roles.
- **Sign-in sessions, kept by the Manager.** Today a sign-in cookie is self-contained: signing out only deletes it
  from that browser. A copy of it (or a VS Code cookie, up to 12 hours) keeps working after signing out, unless the
  password changes. With sessions:
  - signing out ends that session on the server: its Portal cookie and its VS Code cookies stop working;
  - a list of your signed-in devices (browser, IP, last seen) that you can sign out;
  - "remember me" shorter than the current 30 days (suggested: 7 days sliding).
  **Review proposal:** move the underlying revocable-session support to phase 1, independent of choosing a second
  factor. Portal cookies, editor tickets and editor cookies refer to the same server-side session. Logout, session
  revocation and expiry close existing editor/terminal connections too. Define an absolute lifetime as well as idle
  or sliding expiry. Device-list UI can follow later. This adds no extra sign-in when opening VS Code.
- **Asking again before sensitive actions** (password or code): viewing or setting a Runner password, changing roles,
  resetting someone's password, deleting a user.

## 5. Reverse proxy: nginx proxy manager

For `dam.devedse.click` and `dam.devedse.duckdns.org`. HTTPS stays in the proxy; HTTPS inside the Manager is not
planned.

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
  The final deployment intentionally removes this extra login; DAM enforces authentication itself.

**VS Code proxy host**, with a wildcard name so each Runner gets its own host (section 2):

- **One wildcard NPM entry covers every Runner. Never require an NPM entry per Runner.** Configure the wildcard
  DNS record, certificate and proxy host once for each public domain used for editors. New Runners automatically get
  their own editor hostname through the same wildcard; they require no NPM, DNS or certificate changes.
- Example for the `.click` domain:

  | Configure once | Value |
  |---|---|
  | Wildcard DNS record | `*.code.dam.devedse.click` pointing to the public address of NPM |
  | NPM proxy host domain | `*.code.dam.devedse.click` |
  | Forward destination | Manager's address, using HTTP |
  | Forward port | `8081` today; `8080` after hostname routing is implemented |
  | WebSockets / asset caching | On / off |
  | Certificate coverage | `*.code.dam.devedse.click`, obtained with a DNS challenge |

  `runner-01.code.dam.devedse.click`, `runner-02.code.dam.devedse.click` and future Runner names all use this
  single entry. NPM preserves the requested hostname. DAM matches it to the Runner, validates the signed-in user's
  access, and rejects unknown names or a Runner ID in the URL that does not match the hostname. Routing to DAM is
  not permission to open VS Code. Each hostname remains a distinct browser origin despite sharing one proxy entry.
  The wildcard certificate covers these Runner names, not deeper nested subdomains or the bare `code.dam` host.
- Domain `*.code.dam.devedse.click` (and/or `*.code.dam.devedse.duckdns.org`), forward to the Manager host: port
  `8081` today, port `8080` once VS Code is served by hostname (phase 1). Websockets Support on, Cache Assets off.
- A wildcard certificate needs a Let's Encrypt DNS challenge (nginx proxy manager supports DuckDNS and most DNS
  providers). The name must resolve: DuckDNS resolves every name under `devedse.duckdns.org` already; for
  `devedse.click` add a wildcard record `*.code.dam` pointing to the proxy.
- Compose: `EDITOR_URL=https://{runner}.code.dam.devedse.click`. Until `{host}` exists (phase 1), choose one domain:
  from the Portal on the other domain, VS Code still works through **Open in a new tab**. Embedded in the Portal, VS
  Code only works when it is the same site as the Portal (`code.dam.devedse.click` with `dam.devedse.click` is).
  Once VS Code is served by hostname, the `8081` port line can go from the compose file.
- Without a wildcard: one host, e.g. `code.dam.devedse.click`, and `EDITOR_URL=https://code.dam.devedse.click`. It
  works, but Runners opened in the same browser can reach each other's VS Code (section 2); not for internet-facing.

**After phase 1 (review proposal):** configure NPM's actual connection address as a trusted proxy, and the two public
Portal origins. The UI should explain these settings and help identify the proxy address, without automatically
trusting headers from every private address. Verify normal pages, WebSockets, uploads and forwarded HTTPS/IP behavior
on the installed NPM version. Local IP access remains available through the intended LAN path.

## 6. Phase 3 — review and checks

- **Dependency check in CI (design pending):** explicitly fail on the chosen vulnerability severities, for example
  with NuGet audit warnings promoted to errors; listing vulnerable packages alone is not a failure policy. Include
  transitive dependencies and decide how audit-source failures are handled. Also cover bundled browser dependencies
  and container packages; rebuilding an image alone does not prove they are patched.
- **Automated scan:** an OWASP ZAP baseline scan against a test instance, signed in and not. Baseline scanning is
  passive; supplement it with targeted authorization tests using two users, two Runners and a hostile editor page.
- **Review** of everything that answers without a sign-in: the sign-in page and form, static files, `/health`, the VS
  Code ticket and ping endpoints, WebSocket upgrades.
- **Tests** for each phase-1 item: VS Code only on its matching hostname, the Portal never on a VS Code hostname,
  tickets refused the second time and after their lifetime, forged forwarded headers from an untrusted address, form
  posts from another origin, rate limits, headers present, setup code required.

## 7. Decisions

- 2026-10-07 — HTTPS stays in the reverse proxy.
- 2026-10-07 — VS Code keeps an origin of its own, never a path on the Portal's host; it can share the Portal's port,
  chosen by hostname. Internet-facing setups use one hostname per Runner.
- 2026-10-07 — VS Code needs Use access to the Runner (it is a shell there), not only access to an agent.
- 2026-10-07 — Phase 1 starts with VS Code by hostname, since it changes the proxy setup.
- 2026-10-07 — Review: preserve convenient local IP access; the user accepts local limitations. No proxy Basic auth
  in the final public setup. Strip incoming proxy/browser authentication headers before forwarding to Runners.
- 2026-10-07 — Review: coordinated guessing protection accepted; thresholds and recovery behavior still need design.
  CI/scan improvements are under consideration. Origin allowlists, antiforgery/cookie hardening and moving revocable
  sessions earlier are recommendations being explained, not yet settled configuration/UI decisions.
- 2026-10-07 — One wildcard proxy setup per editor domain handles all Runners. Adding Runners must never require
  individual NPM entries, DNS records or certificates. The user authorized a new branch and PR and delegated full
  implementation to a Sol 6.1 agent with Medium reasoning.
- Open: second factor (TOTP first?), requiring it, session lifetime, re-authentication for sensitive actions.
