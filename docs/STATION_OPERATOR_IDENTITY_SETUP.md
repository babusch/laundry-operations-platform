# Development station operator identity: guided setup

Status: identity-only backend proof implemented 2026-10-18; real Keycloak/browser sign-in, safe identity status and local sign-out verified by the user 2026-10-22. Real timed expiry and outage verification remain pending.

Local sign-out verification (2026-10-22): the user supplied console output showing **204** for the CSRF-protected logout and then **401** for `/operator/session`. The console labels that 401 as an error, but it is the intended rejection of an ended session. No token or credential output was needed. This confirms local browser sign-out, not provider-wide logout or station-wide cross-browser ownership/revocation.

User verification (2026-10-22): the successful callback returned `authenticated: true`, display name `management-demo`, UTC expiry and `operationalAccessEnabled: false`. This is an identity proof only; it grants no business permissions. Earlier activation/pending-login notes below record the preceding steps, not the current login result.

Activation update (2026-10-22): the user reports the separate client imported and named secret saved. The gateway was restarted with `Start-DevelopmentOperatorIdentity.ps1 -WithPreview` without inspecting the private file. Readiness/root are 200 and anonymous proof status is 401. Real login, provider acceptance of this credential and logout/expiry/outage remain pending user verification. Earlier manual-setup instructions below remain the procedure for a fresh installation; do not reimport an existing client.

This step proves **sign in → display safe identity status → sign out locally**. It does not add PWA sign-in buttons, scanning permission, a shift-length session, inactivity locking, password unlocking, station takeover or receiving. See [ADR 0021](decisions/0021-development-station-operator-identity-proof.md).

## The two passwords/secrets are different

- The **operator-development client secret** identifies the gateway application to Keycloak. It is separate from the management/gateway client secrets and from every person's password.
- The **account password** identifies a person. Type it only into Keycloak's login page. An existing local test account can prove identity without receiving any application permission; no new account is required for this proof.

Do not paste either into chat or commit them. Store the client secret in your password manager or ignored private `.env`; never in the import JSON or `.env.example`. The helper consumes only `OPERATOR_CLIENT_SECRET`, never evaluates the environment file or prints the value. Your account password does not belong in `.env` for this setup.

## 1. Import the new client — first manual step

Keep the existing identity service/database; do not reset volumes or replace the realm. Open [Keycloak administration](https://localhost:8443/admin/) and sign in with your existing local administrator.

1. Select the **laundry-development** realm, not **master**.
2. Open **Clients**, then **Import client**.
3. Select `deploy/local/operator-development-client.json` from this repository and import it once.
4. If `operator-development` already exists, inspect that client rather than creating a duplicate. Do not change `management-development` or `gateway-development`.

The supplied import contains **no secret**. It enables confidential authorization-code login with PKCE S256, only the exact callback `https://localhost:7200/operator/signin-oidc`, optional `profile`, no wildcard origins and no direct/password grants, implicit flow, service account or application permission grant.

## 2. Save this application's client secret

Open **operator-development → Credentials**. Copy its generated client secret into your password manager. If desired, add exactly one entry to your existing ignored repository-root `.env`, preserving its other entries:

```dotenv
OPERATOR_CLIENT_SECRET=your-local-development-client-secret
```

The value above is a placeholder, not a usable credential. Do not copy the example file over your private `.env`. Alternatively, the helper can request the client secret with hidden input. These instructions do not authorize agents to inspect your private file.

## 3. Start the proof after the client is ready

The current gateway may already own port 7200. Identify and stop only that development gateway first; the helper refuses to stop a process for you or start a second listener. Leave PostgreSQL and Keycloak running. From repository-root PowerShell:

```powershell
.\deploy\local\Start-DevelopmentOperatorIdentity.ps1 -WithPreview
```

`-WithPreview` retains opt-in synthetic lookup/scan preview/local-order viewing. It does not grant source permissions. The helper enables only this Development proof and explicitly disables cloud forwarding. It restores process environment settings on exit, never changes accounts/permissions, migrates data, enrolls a source or modifies private files. Use `-PromptForSecret` to ignore saved settings and enter the **client secret**, not the account password. TLS trust must remain valid; do not bypass certificate warnings.

## 4. Verify in your already enrolled browser

Open [operator sign-in](https://localhost:7200/operator/login) in the browser already enrolled at this gateway. An unenrolled browser receives 401; operator login does not enroll it automatically.

Sign in with an existing local test account. The browser should return to `/operator/session` with JSON like:

```json
{
  "authenticated": true,
  "displayName": "your-test-account-name",
  "expiresAtUtc": "a UTC timestamp",
  "operationalAccessEnabled": false
}
```

These values illustrate the shape only. The real expiry comes from the login time; the optional name can be null when a safe profile name is unavailable. `operationalAccessEnabled: false` is intentional: this proves identity, not scanning authorization. No token, account subject, role, source ID or secret is returned.

## 5. Verify local sign-out

While on the successful `https://localhost:7200/operator/session` page—not a browser error page—open the browser's developer console. This intentionally calls only local sign-out with a fresh operator-bound CSRF token:

```javascript
var csrfResponse = await fetch('/operator/antiforgery', { cache: 'no-store' });
if (!csrfResponse.ok) throw new Error('Operator session is unavailable; sign in again.');
var csrf = await csrfResponse.json();
var logoutResponse = await fetch('/operator/logout', {
  method: 'POST',
  headers: { [csrf.headerName]: csrf.requestToken },
  cache: 'no-store'
});
console.log('Local sign-out status:', logoutResponse.status);
console.log('Session status after sign-out:', (await fetch('/operator/session', { cache: 'no-store' })).status);
```

Expected: **204**, then **401**. This removes the server ticket and clears only the operator-proof cookie. It does not sign out Keycloak globally, remove source enrollment, complete an order, discard accepted work or call the cloud. Other tabs sharing that cookie lose the proof on their next request.

## Scope and verification limits

### Browser callback compatibility (2026-10-22)

The user's initial real browser login reached `/operator/signin-oidc` but returned 403. Public-header diagnosis verified Keycloak's `Referrer-Policy: no-referrer`; browsers can consequently send its `form_post` response with `Origin: null`. The original origin guard rejected this before OIDC validation. A synthetic signed regression reproduced that failure; no account password, private environment value or real authorization response was inspected.

The guard now permits the opaque (`null`) or exact local provider origin only for a POST to the exact `/operator/signin-oidc` path with `application/x-www-form-urlencoded` content. Ordinary login/status/logout routes retain their origin restrictions; TLS, fixed host and loopback checks still apply. This does not enable CORS or trust an opaque origin as authentication. State/correlation, nonce, code/PKCE, token signature/issuer/audience/lifetime and enrolled-source binding remain mandatory. Unsolicited/malformed callbacks fail without creating an identity proof.

After restarting with this fix, start a **fresh** login at `/operator/login`; do not refresh or replay the failed callback. Previously issued callback parameters may already be consumed or expired. A successful user retry is still required before marking real login verified. No client-secret change, permission grant or realm modification is required for this correction.

References: [browser referrer policy and Origin](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Referrer-Policy#effect_on_the_origin_header), [OpenID form-post response mode](https://openid.net/specs/oauth-v2-form-post-response-mode-1_0.html).

The test session has a fixed **15-minute expiry**, without sliding renewal. This is not the future 15-minute inactivity lock or a shift-length session. Tickets are memory-only, capped at 256 live proofs with expired-ticket cleanup. Gateway restart invalidates all proofs. Cross-browser single-operator station ownership, durable session/audit lifecycle and stale pending-login cancellation belong to the next reviewed station-session step; this proof must not authorize business commands.

Already valid status and local sign-out need only the gateway's memory/source database, not Keycloak. A new login requires reachable Keycloak; missing identity/source storage fails safely. Losing internet while local identity remains available is different from losing identity itself. Production offline password unlock and shift changes remain unresolved. No claim of production offline readiness is made.

Automated verification: **338 gateway tests passed** (29 new identity/helper-import/store cases; signed synthetic provider with real framework handlers and disposable Docker plant database), **149 station tests passed**, generated OpenAPI client/typecheck and station typecheck passed. The separate helper passed nine synthetic cases on both PowerShell 7 and Windows PowerShell 5.1. C# whitespace formatting, PowerShell syntax and `git diff --check` passed. An initial test-host configuration failure and a helper mock-variable collision were corrected before those successful runs.

Real client provisioning, browser login/logout/expiry, internet-isolation tests and full Keycloak outage/recovery remain **not run**. Mocked-provider tests are not real Keycloak verification. No PWA UI was changed, so no new visual screenshot/check is claimed. Cloud/management regression suites were not rerun; live services, credentials, permissions and stored business data were not changed. The earlier station preview remains available on the unchanged running gateway; the new proof stays disabled until manual setup/activation.
