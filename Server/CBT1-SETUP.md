# ShadowPlay NEXT — CBT1 tester portal

Implemented endpoints: `/v1/auth/register`, `/v1/auth/login` (existing Duluka credentials),
`GET /v1/cbt1/status`, `GET /v1/cbt1/me`, `POST /v1/cbt1/apply`,
`GET /v1/cbt1/admin/applications`, `POST /v1/cbt1/admin/applications/{id}/decision`,
`DELETE /v1/cbt1/admin/applications/{id}`.

## Lifecycle

1. User registers or logs in with an **existing Duluka username and password**.
2. The browser holds the opaque bearer session **in memory**, and a 256-bit
   device key **in sessionStorage** for the current tab. Refresh requires login.
3. Applications require a valid server session with a **native Username**
   and are tied to its immutable `AccountId` (not a client-provided username).
4. One application per AccountId; the user can see **their own** application
   and decision through `/v1/cbt1/me`.
5. Only a separate administrator token can list, decide, or delete applications.
   An `approved` status is **not** an authorization grant for builds/downloads.
6. Registration remains **closed by default**.

## Activation (operator only)

- Public page: https://scotcsduluka.github.io/ShadowPlay-NEXT/cbt1.html
- Ensure the server is reachable through a **trusted HTTPS reverse proxy /
  Cloudflare Tunnel**, backed by **loopback-only** HTTP; never forward
  unauthenticated admin consoles or port-forward raw HTTP.
- Set `DULUKA_Cbt1__Enabled=true` **only after** confirming the server and
  privacy/contact operations are ready.
- Set `DULUKA_Cbt1__AdminToken` to an independently generated random secret
  of at least 32 characters **in the server environment only**.
- Public `gh-pages/cbt1-config.js` sets only the **HTTPS API base URL**:
  `window.CBT1_CONFIG = Object.freeze({ apiBase: "https://<trusted-host>" });`
  (without a trailing slash). Never commit tokens, cookies, passwords, or
  other secrets to this file.
- CORS permits only `https://scotcsduluka.github.io`. `/v1/auth/register`
  and `/v1/auth/login` inherit that policy, as do CBT1 endpoints.
- For public use, keep `PublicApi` loopback/tunnel safeguards in the local
  server deployment. Current uncommitted local changes to this behavior
  must **not** be lost in a Git pull/rebase. Merge carefully.
- Existing CBT1 data, if any, is preserved by additive `AccountId` and
  `Username` columns; old anonymous applications remain unlinked.

## Privacy, abuse and test status

- Capture alias, contact email, Windows family, GPU family and testing focus.
  Avoid hardware UUIDs, account passwords, and unnecessary personal details.
- SQLite is private under `AppData/CBT1Applications.db` by default.
- Applications are rate-limited (3 per source IP/hour). Native auth already
  has independent rate limits. In tunnel mode, use the trusted verified
  client IP instead of treating all requests as a shared loopback IP.
- Administrator APIs require `Authorization: Bearer <admin-secret>`;
  reviewer UI is not included.
- Isolated verification performed on 2026-10-24 using
  `http://127.0.0.1:5116`, separate test databases and the Release build:
  **0 build warnings/errors** and full smoke checks passing for register,
  login, auth guard, application persistence/idempotency, own-only retrieval,
  no anonymous admin access, CORS preflight, revoke and re-login.
  No live public end-to-end login/deployment was verified.
