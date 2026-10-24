# Duluka Account — v0 auth/security slice

Central account system for the Duluka ecosystem (NVIDIA ShadowPlay, Duluka Web,
future products). **This directory is a separate product — no ShadowPlay code
depends on it and none was modified.**

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
