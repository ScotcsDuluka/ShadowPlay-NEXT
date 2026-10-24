# Duluka Account — v0 auth/security slice

Central account system for the Duluka ecosystem (NVIDIA ShadowPlay, Duluka Web,
future products). **The server remains a separate service and code owner.** The
Launcher now has a fixed-URL Account button that opens its external HTTPS Portal,
but Launcher does **not** embed passwords, account tokens or direct authentication
API calls. This makes an account entry point available without claiming
ShadowPlay's Capture/OSC engine depends on Server uptime.

> **Current Portal boundary (24 October 2026):** The latest shared-account
> Tailscale Portal uses the main account database for existing-user login,
> while public account registration and CBT1 application submission remain
> **disabled**. The independent Local Admin stays on loopback-only port 5117.
> A portal page loading successfully is not evidence of an externally verified
> full login journey or an open tester recruitment program. Later sections
> document older disposable staging and historical rollout steps; distinguish
> them from the "Unified Duluka Account Portal" section.

Architecture discovery: see the C/4 + C/5 + C/6 v0.1 design (data model, server
modules, security model). Implemented here: the **C/6 auth/security slice**.

## Implemented (v0)

- **GitHub OAuth** — Authorization Code + PKCE (S256) + one-time state (10 min TTL),
  server-side code exchange, redirect-uri = the single configured value,
  identity = GitHub `id` (numeric, stable; login/email are display-only)
- **Account provisioning** — unknown identity creates a NEW account; known
  `(ProviderKey, ProviderUserId)` logs into ITS account; the unique-anchor race
  converges on one account (constraint + fallback re-read)
- **Sessions** — opaque 256-bit tokens (`duluka_st_…`), SHA-256 hashed at rest
  (raw token never stored), device-bound, exactly ONE active session per
  device (a second login supersedes the first, contract §5.2), sliding 7d /
  absolute 30d (both configurable), revoke per-session / per-device /
  logout-all
- **Devices** — client-generated 256-bit device key (hashed at rest), revoked
  keys can never silently re-register, cross-account key reuse rejected
- **Provider unlink** — last-provider guard (409), credential row destroyed,
  sessions issued via that link revoked; a re-login with a previously
  unlinked identity returns to the SAME account (a returning identity
  re-enters its own home on a fresh link period; the anchor is a partial
  unique index over ACTIVE links, schema v2 migration rebuilds v1
  databases). A never-seen identity with a device key bound to another
  account is still refused pre-persist (no orphan account/link).
- **NVIDIA provider = reserved** — `ProviderKeys.Nvidia` exists but has NO auth
  flow. Hardware-derived identity (GPU UUID/serial/driver/fingerprint) is
  explicitly banned as an auth identity (spoofable = bypass).
- **Native accounts + profile** — username/password (PBKDF2, canonical anchor),
  display name + profile image (avatar data URL) editable via
  `PUT /v1/account/profile`; the profile is PRESENTATION-ONLY — AccountId,
  username, devices and sessions are structurally untouched by a profile edit.
  Profile image: `data:image/png|jpeg|webp;base64` only, 256 KB decoded cap,
  validated before base64 decode; schema v4 adds the column additively
- **Rate limiting** — per-IP fixed window: 10/min on auth starts, 240/min on API
- **Error envelope (contract §7.1)** — every response carries
  `ok`, `reqId` (client-issued `X-ReqId`, echoed verbatim), `errorCode`,
  `httpStatus`, `retryable`; payloads sit under `resource`. Errors use the
  §7.2 registry codes (`auth.session_expired`, `auth.session_revoked`,
  `perm.device_removed`, `nf.link`, `conflict.link_conflict`, `server.internal`)
  plus documented extensions for 400-family input errors (`invalid_*`,
  `provider_*`, 501 `provider_reserved`, 429 `server.rate_limited`)
- **Secret redaction** — single implementation (`Security/Secrets.cs`); raw
  tokens/codes/state never logged. The OAuth `state` is returned RAW in the
  start response (it is the client's own correlation value; redaction is a
  LOG rule, §9.2)

## NOT implemented in v0 (deliberate)

- Sync endpoints (`/v1/sync/{product}`) — schema table exists, endpoints next phase
- Provider token persistence — tokens are used transiently and discarded
  (`CredentialReference` table exists, v0 writes no rows)
- Migration framework — v0 applies idempotent DDL at startup + `SchemaHistory`
- E2E encryption for sync blobs — open decision
- Web client (cookie transport) — v0 returns the session token in the JSON body
  (ShadowPlay-first)

## Configuration

| Key | Source | Notes |
|---|---|---|
| `GitHub:ClientId` | config or env `DULUKA_GitHub__ClientId` | non-secret |
| `GitHub:ClientSecret` | **env `DULUKA_GitHub__ClientSecret` only** | never in config files |
| `GitHub:RedirectUri` | config | must exactly match the GitHub app setting |
| `Database:Path` | config | default `AppData/DulukaAccount.db` (gitignored) |

## Run

```text
dotnet run --project .\Duluka.Server\Duluka.Server.csproj -c Release
dotnet run --project ..\Tester\test\Duluka\Server\Duluka.Server.Tests.csproj -c Release  # 59 tests
dotnet run --project ..\Tester\test\Duluka\HttpIntegration\Duluka.Http.Integration.Tests.csproj -c Release  # 135 black-box tests
```

## Black-box HTTP regression (C/2)

`Duluka.Http.Integration.Tests` launches the real server binary per test group (fresh
SQLite per group, loopback bind, per-group auth-start rate-limit budget) and pins the
live HTTP contract: health/readiness, auth flows, account/provider/device/session
lifecycles, revoke cascades, idempotency, store-level CAS races, rate limiting, restart
persistence, the 401/404/409 matrix and the error envelope. GitHub identity exchange is
never faked; the one egress-dependent group is honestly SKIP-gated.

Contract violations found by the suite are tracked in the mismatch ledger
(M-1..M-11 — only M-9 remains a confirmed contract mismatch in
the October 24, 2026 live run) in
[HTTP integration TEST-MATRIX.md](../Tester/test/Duluka/HttpIntegration/TEST-MATRIX.md) —
each is a fix required in `Duluka.Server`, not in the tests. A test passes only while the
tracked violation still reproduces exactly, so the ledger can never go stale silently.

Endpoints: `POST /v1/auth/github/start` → GitHub authorize URL →
`POST /v1/auth/github/callback {code, state, deviceKey}` → `{sessionToken,…}`;
then `GET /v1/account/me`, `PUT /v1/account/profile`,
`GET|POST|DELETE /v1/account/providers[…]`,
`GET /v1/account/devices`, `POST /v1/account/devices/{id}/revoke`,
`POST /v1/auth/session/{refresh,revoke}`, `POST /v1/auth/sessions/revoke-all`.

## Ports

- **Duluka HTTP API binds `http://127.0.0.1:5115`** (appsettings `Urls`).
- Port **5000 is RESERVED by the ShadowPlay TCP Hub** on the same machine —
  never bind Duluka there (a hub/Duluka collision silently kills the
  overlay's client connections).
- Clients override the base with the `DULUKA_API_BASE` environment variable.

## Public API deployment (October 24, 2026 security hardening)

DO NOT port-forward port 5115 or the spare TOT DDNS port 15242. API
authentication sends passwords/session tokens, so raw HTTP over LAN/Internet
is NOT suitable for production. The public switches in run-server.ps1 and
build-and-run.bat are now intentionally blocked.

**Recommended topology:** client -- HTTPS --> Cloudflare edge -- encrypted
Cloudflare Tunnel --> local cloudflared -- localhost HTTP -->
`http://127.0.0.1:5115` (Duluka.Server). The loopback hop stays on the same
machine; there is NO inbound firewall rule, NAT port forwarding, or open WAN
port. A hostname in a Cloudflare-managed DNS zone is needed for a named
tunnel. The TOT DDNS host alone is not a Cloudflare-managed zone.

Public API mode: `DULUKA_PublicApi__Enabled=true`, set by
`start-public-tunnel.ps1` (NOT enabled by the normal run-server scripts).

- It rejects non-loopback listener URLs at startup (fail-closed).
- It does NOT register any `/admin` routes; both admin page and admin API
  return 404, including if a proxy rewrites the Host header.
- Every non-health request requires loopback cloudflared, verified single
  `CF-Connecting-IP` and `X-Forwarded-Proto: https`. Missing/insecure
  edge headers return 403. Never enable this mode behind an untrusted local
  proxy that permits clients to spoof these headers.
- Auth/API rate limits partition by validated `CF-Connecting-IP`, not the
  shared `127.0.0.1` tunnel address.
- Kestrel request bodies are capped at 1 MiB.
- This is server-side defense-in-depth, NOT a substitute for HTTPS at the
  Cloudflare edge, CSRF review, account audit, WAF/rate limits or client
  credential storage review.

**Setup once** (do not paste tunnel tokens into tracked files):

1. Install official `cloudflared` and create a remotely managed tunnel in
   the Cloudflare dashboard; route a PUBLIC HTTPS hostname to
   `http://127.0.0.1:5115`. Force HTTPS at Cloudflare's edge.
2. Set `DULUKA_CLOUDFLARE_TUNNEL_TOKEN` as a secret environment variable,
   or set `DULUKA_CLOUDFLARE_TUNNEL_TOKEN_FILE` to an ACL-protected file.
3. Run `powershell -File .\start-public-tunnel.ps1 -CheckOnly` to check
   prerequisites, then run `powershell -File .\start-public-tunnel.ps1`
   to start the server and tunnel together, shutting down both on exit.
   Cloudflared reads the secret from an environment variable/token file,
   not from a process command-line argument.
4. Point desktop/web clients at the HTTPS hostname using `DULUKA_API_BASE`.
   Update the GitHub OAuth redirect registration as needed for the intended
   client flow; v0 currently uses a native localhost callback.
5. Run `powershell -File .\verify-public-api.ps1` to exercise security
   gates against an ISOLATED temp SQLite store. This is local smoke testing,
   NOT a full external end-to-end Cloudflare/production review.

**Admin:** run the ordinary local server without `PublicApi` to use
`http://127.0.0.1:5115/admin`. NEVER tunnel the admin interface. The
current admin header + loopback check is not real operator authentication;
administrative actions should not be published until proper admin identity
and authorization are implemented. Do not run both modes on the same port.

**Open items:** hosted DNS hostname and tunnel token provisioning, public
end-to-end HTTPS tests, stronger admin auth, password recovery / MFA policy,
safe browser cookie sessions / CORS, database backup/restore, audit logging
and deployment monitoring. Until those are addressed, do not claim general
production readiness.

## Tailscale Funnel (staging-first, Oct 24 2026)

Tailscale is installed on AORUS-DESKTOP (v1.104.1) and its MagicDNS name is
`aorus-desktop.taile6314b.ts.net`. Tailscale Serve (tailnet-only) and Funnel
(public internet) provide HTTPS without the `totddns.com` hostname, port
forwarding or Cloudflare DNS ownership. Funnel is publicly reachable by
ANYONE with the URL, so keep it disabled except while actively testing.

**Two separate local ports:**
- Cloudflare origin: `127.0.0.1:5115` (existing service; unchanged).
- Tailscale staging origin: `127.0.0.1:5116` (isolated account/CBT1 databases).

**What was implemented:**
- `DULUKA_PublicApi__Proxy=tailscale` plus `DULUKA_PublicApi__Hostname=<node.ts.net>`.
- Fail-closed loopback binding, `X-Forwarded-Proto: https`, exact
  `X-Forwarded-Host`, single validated `X-Forwarded-For`.
- Per-forwarded-IP rate limiting; the IP header is trusted ONLY for traffic from
  loopback Tailscale proxy. Do not connect other untrusted local proxies.
- Block both `/admin*` and `/v1/cbt1/admin*` before any endpoint handlers.
- Staging has `Cbt1:Enabled=false`, uses separate SQLite files and never
  touches the real account DB.

**Easy launcher:** double-click `Server\Tailscale-Launcher.cmd` and choose Check / Security Test / Public STAGING / Stop. It never exposes the API without a separate Y confirmation.

**Safe commands** (from `Server` directory):

```powershell
& .\start-tailscale-funnel.ps1 -CheckOnly       # read-only preflight
& .\verify-tailscale-api.ps1                  # isolated localhost tests
& .\start-tailscale-funnel.ps1 -PublicTest      # TEMPORARY public staging API
& .\start-tailscale-funnel.ps1 -Stop            # stop Funnel and staged server
```

The `-PublicTest` command is foreground; Ctrl+C is intended to close Funnel and
stop the staged origin. The `-Stop` switch is provided for manual cleanup.
Do not use real accounts, passwords or personal information with the staging
URL. It is not production. Full published-domain verification from a device
outside the tailnet and a password recovery / anti-abuse review remain pending.

**Evidence from Oct 24:** isolated proxy smoke 23/23; real Tailscale
Serve HTTPS checks returned `/healthz/ready=200`, `/admin=404`,
`/v1/cbt1/admin/applications=404`, unauthenticated `/v1/account/me=401`.
The Funnel CLI advertised a public HTTPS endpoint and its JSON config set
`AllowFunnel=true`, but a separate off-tailnet client verification is
still required.

## Duluka Account Portal (Tailscale staging)

The HTTPS Funnel root now redirects to the embedded, NVIDIA-style tester portal.
Example host on this machine: `https://aorus-desktop.taile6314b.ts.net/`.

- `/` redirects to `/portal` in public API mode.
- `/portal`, `/portal/app.js` and `/portal/app.css` are embedded in
  `Duluka.Server.dll`. No external CDN, tracking, script runtime or separate
  static-file service is needed.
- UI supports username + password register/login and a session-scoped browser
  token; the persistent device key is randomly generated with Web Crypto.
- Tester can read their own CBT1 application, submit it when enabled and log out.
- Staging Funnel deliberately leaves `DULUKA_Cbt1__Enabled=false` by default;
  test the submit route only with `verify-portal-flow.ps1`, which uses synthetic
  data and disposable SQLite databases.
- Operator routes `/admin*` and `/v1/cbt1/admin*` remain hidden in public
  mode. Portal sends security headers including a strict same-origin CSP.
- Run `Tailscale-Launcher.cmd` to check, run all tests, open a temporary
  public staging process, stop it, or open the Portal in a browser.
- Do NOT treat staging credentials or database persistence as production.
  Opening public registrations needs further abuse review, external-client
  verification, and production identity/account migration planning.

Validation: build 0 warnings/errors; Node.js syntax check for portal JS;
27/27 loopback proxy security checks; 12/12 isolated CBT1 flow checks;
HTTPS response confirmed for root/page/CSS/JS/status; admin routes 404.
Off-tailnet HTTPS GET verification passed from HUAWEI-MateBook (Tailscale
not installed): root=200, app.js=200, CBT1 status=200, admin=404,
unauthenticated account/me=401. Full off-tailnet signup/login remains
untested; isolated local signup/login/CBT1/logout integration passes.


## Local Admin Dashboard — separate localhost port

The PublicApi builds intentionally respond HTTP 404 for `/admin` on ports 5115
(Cloudflare origin) and 5116 (Tailscale staging). **Do not remove that rule.**

A dedicated local dashboard process runs at `http://127.0.0.1:5117/admin`.
It is never connected to either public tunnel, and its middleware requires a
literal localhost Host header, loopback remote address, and exact local bind.
Only `/admin*` and `/healthz*` are reachable in `LocalAdmin:Enabled` mode.
The admin browser API still requires `X-Admin-Request: 1`; no CORS policy
applies to admin routes. The same original SQLite account DB is explicitly
used; operators should be aware that admin actions can change or delete real
accounts.

Quick shortcuts: double-click `Server\Local-Admin.cmd` to start/open;
double-click `Server\Local-Admin-Stop.cmd` to stop. The underlying script is
`Server\start-local-admin.ps1`, with `-CheckOnly`, `-NoOpen`, and `-Stop`.
The disposable security check is `Server\verify-local-admin.ps1`; it must be
run when the local admin instance is stopped because it uses port 5117.

**Historical build note:** The conflict state described below occurred during an
older AdminLocal rollout. A later Git check found **zero unmerged index entries**;
this paragraph is retained only to explain the temporary isolated build and
is **not a current instruction to treat the repository as unmerged**.

**Important Git state on Oct 24:** another in-progress merge introduced conflict
markers into `Program.cs` and `CBT1/Cbt1Registration.cs`. To avoid modifying
the unfinished merge, the currently working AdminLocal executable was built
from a temporary conflict-resolved snapshot (Program.cs HEAD side and
CBT1 imports remote side). The conflicted working-tree source has NOT been
resolved or staged by this Admin task. Before rebuilding the canonical project,
resolve both Git conflicts while keeping LocalAdmin middleware, the restricted
`app.UseCors()` call, and the public-admin 404 guard. Do not build the
canonical project until source is conflict-free. The current AdminLocal binary
is operational and was independently smoke-tested.

## Unified Duluka Account Portal — Tailscale + Local Admin

**Current architecture (2026-10-24):**

| Service | Binding / URL | Data |
| --- | --- | --- |
| Account API | `127.0.0.1:5115` (Cloudflare origin) | Existing main `DulukaAccount.db` |
| Shared Portal | `127.0.0.1:5116` via `https://aorus-desktop.taile6314b.ts.net/` | **Same main `DulukaAccount.db`** |
| Local Admin | `127.0.0.1:5117/admin` (not tunneled) | **Same main `DulukaAccount.db`** |

Main DB: `Server/Duluka.Server/bin/Release/net10.0/AppData/DulukaAccount.db`.
Public Portal **does not copy or migrate** the account database. Each
account retains its username, password verifier, account ID, devices,
provider links, and session history. The HTTP API uses separate .NET
processes with short-lived SQLite connections sharing the one main DB.

### Safeguards

- Public Portal always binds `127.0.0.1:5116`; Tailscale Funnel supplies HTTPS.
- The existing `PublicApi:Proxy=tailscale` middleware checks the loopback
  address, HTTPS forwarding, client IP and expected *.ts.net hostname.
- `/admin*` and `/v1/cbt1/admin*` are 404 from Funnel. The local
  administrative dashboard on 5117 remains isolated from Funnel.
- **Public registration OFF:** `PublicApi:AllowRegistration=false` blocks
  `POST /v1/auth/register` on the main-account Funnel and hides the
  registration tab. This does not disable existing-account login or
  affect the standalone main-account service on 5115.
- **CBT1 OFF:** `Cbt1:Enabled=false` in the shared Portal process; CBT1
  application state uses a distinct, unused file, not the main account DB.
- Browser sessionStorage and device key names are separated by backend mode
  (`main` vs `staging`), avoiding staging-token reuse after cutover.
- Do not submit real secrets to the chat or test console. Password resets
  use the already-secured **Local Admin** recovery flow at port 5117.

### Start, stop and rebuild

```powershell
& .\start-shared-account-funnel.ps1 -CheckOnly   # non-destructive checks
& .\start-shared-account-funnel.ps1 -Start       # foreground shared DB Funnel
& .\start-shared-account-funnel.ps1 -Stop        # stop ONLY Funnel + shared Portal
& .\verify-tailscale-api.ps1 -BinaryPath '.\Duluka.Server\bin\TailscaleShared\Duluka.Server.exe'
& .\verify-full-regression.ps1
```

Double-click `Server/Tailscale-Launcher.cmd` for the interactive menu.
Menu [3] starts the main DB Portal, [4] stops it, [5] opens its URL,
and [7] is reserved for **separate disposable STAGING** only.
The older `start-tailscale-funnel.ps1` still uses disposable databases.
Never run both on port 5116; use the launcher or dedicated scripts.

Main Portal was compiled to `Duluka.Server/bin/TailscaleShared` without
changing the running executable on 5115 or 5117.
Before first cutover, `Server/Tools/Duluka.DbBackup` performed an SQLite
online `BackupDatabase`, checked `PRAGMA integrity_check=ok` and the
account row count, then placed an access-restricted file under
`%LOCALAPPDATA%/DulukaAccount/Backups`. Backups contain private account
information: do not publish them or check them into Git.

**Verification:** isolated original proxy checks 27/27 passed;
main-mode startup on a temporary clone of the main DB passed
(portal config, /admin 404, /cbt1/admin 404, session 401,
public registration 403, CBT1 closed). Live Funnel was cut over to the
main DB after old disposable staging was stopped; separate HUAWEI-MateBook
outside the tailnet fetched mode=`main`, registration=`false`,
page and JS HTTP 200, admin HTTP 404 and anonymous /me HTTP 401.

Operator must perform an actual browser sign-in with their own password;
the administrator password and recovery codes must NEVER be sent in chat.
The external end-to-end password login for an existing account has not
been run by automation.

## My Account & Connected Devices (2026-10-24)

The HTTPS Portal's signed-in view now includes an Account summary with
`/v1/account/me` identity and current device, plus a connected-device list
from `/v1/account/devices`. It offers an explicit, confirmed Disconnect
action for **other** devices via `/v1/account/devices/{id}/revoke` (the current
device is labeled and has no Disconnect button). The current user can change
their own native password using the existing
`POST /v1/account/password` endpoint, current password, and confirmation.

The UI only uses the signed-in Bearer token from sessionStorage and never
persists or logs passwords. Account IDs/device labels are inserted with safe
DOM `textContent`; no untrusted HTML interpolation. Data is reloaded after
login or browser-tab restoration. Local sign-out revokes the token server-side
and clears client-side account/device/password state.

**Verification:** Build succeeded with 0 warnings/errors, Node JS syntax
check passed, 27/27 public proxy security checks passed. Expanded
`Server/verify-portal-flow.ps1` exercises 23 assertions with a disposable
SQLite DB: register, login, /me, CBT1, two distinct devices, disconnect,
revoked session, wrong/correct current password, new-password login, logout.
The 59+135 original regression suites are independent of live account data.

**Rollout:** `Server/Duluka.Server/bin/TailscaleShared` is the live
Funnel's build destination; use `start-shared-account-funnel.ps1 -Stop`,
build to that isolated output, then `-Start` to preserve public origin
and main SQLite DB. Never rebuild into `bin/Release/net10.0` while its
Cloudflare API executable is running.

## CBT1 Invite-only rollout — 24 October 2026

Cloudflare Pages website: https://nv-cbt.pages.dev/
Local Admin dashboard: http://127.0.0.1:5117/admin (AORUS-DESKTOP only)
Public API: Tailscale Funnel -> 127.0.0.1:5116

### Operator instructions

1. Open Local Admin on AORUS-DESKTOP.
2. Under **CBT1 Invitation Codes**, enter a private label and select an
   expiry (24 hours / 7 days / 30 days); click **Generate Invite**.
3. Copy the newly generated `CBT1-...` code immediately. The browser only
   shows it once, the server stores only SHA-256. Do not paste codes, account
   passwords or other secrets in chat, source code or public issues.
4. Give the code privately to ONE existing Duluka Account user.
5. Tester visits `https://nv-cbt.pages.dev/`, logs in, redeems the code,
   fills out the CBT1 application and submits it.
6. In Local Admin, **CBT1 Applications / Tester approvals** lists submitted
   applications. Set Approved / Waitlist / Rejected, then save.
7. Tester refreshes their account's CBT1 status to see the decision.
8. To cancel a code / invitation, use **Revoke** in Local Admin.

### Important security boundaries

- `Cbt1:Enabled=false`: general applications are closed.
- `Cbt1:InviteEnabled=true` on the **public Tailscale Portal only**,
  allowing code holders to apply. Public native account registration stays
  **disabled**. Existing Duluka login is required; Invite Code does not
  bypass authentication or create an account.
- CBT1 data is stored in a dedicated SQLite file shared by only the
  Local Admin and Tailscale Portal:
  `%LOCALAPPDATA%\DulukaAccount\CBT1\Applications.db`
- Codes are generated cryptographically, single-use, expiring, and stored
  as SHA-256. A redemption is committed transactionally to an account.
- Operator routes live exclusively on loopback Local Admin port 5117,
  require `X-Admin-Request`, and state-changing calls require the exact
  local Origin. They are not present on Cloudflare Pages nor public Funnel.
- Decisions are manual, never assigned merely because someone has a code.
- The original Account SQLite is untouched by the CBT1 invite subsystem.
- The current invite feature is for **existing accounts**. Do not enable
  public new-account registration without a separate invite-gated signup
  flow, abuse controls and privacy review.

### Verification and regression

`Server\verify-cbt1-invites.ps1` runs three isolated server processes
on temporary test accounts and CBT1 database (plus the temporary 5117
operator listener) and checks generation, Origin rejection, one-time use,
second-account reuse rejection, eligible application, approval and revoke.
Stop the real Local Admin briefly before running; restart it afterward.

All new isolation tests pass. The original full regression also passes
59/59 server tests + 135/135 HTTP tests with existing tracked mismatch
M-9 (repeat revoke returns 401 instead of target 200). Cloudflare Pages
Edge tests passed 11/11. Tested the public website from a HUAWEI laptop
without Tailscale: home=200, inviteEnabled=true, CBT1 general accepting=false,
anonymous redemption=401, admin edge route=404.

Do not confuse `Cbt1:InviteEnabled` with `Cbt1:Enabled`: leave general
registration off until the public CBT1 opening decision.
