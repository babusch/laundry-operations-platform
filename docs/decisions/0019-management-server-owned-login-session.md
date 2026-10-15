# 0019 — Management server-owned login session

Status: Accepted for the Development-only identity proof; approved by the user on 2026-10-11.
Date: 2026-10-11.

## Context

The initial management permissions are approved and tested, but interactive human authentication is not implemented. The management frontend baseline is Next.js; the existing .NET cloud API already owns application authorization and gateway authentication. The frontend's login/session hosting must be decided before wiring Keycloak sign-in. This does not decide offline operator login.

## Decision

Use the existing .NET backend as the management application's server-side authentication boundary. Use a separate confidential OpenID Connect client in Keycloak's `laundry-development` application realm, authorization code flow with PKCE, and the framework's standard OpenID Connect/cookie handlers. Add `Microsoft.AspNetCore.Authentication.OpenIdConnect` matching the existing ASP.NET Core dependency baseline when implementing the approved slice. Do not build token cryptography or a custom identity provider.

The browser signs in through Keycloak. The backend validates the login response and establishes a protected `Secure`, `HttpOnly`, host-only management cookie. Tokens and client credentials must not be available to management JavaScript or stored in browser local storage. An initial identity-only proof need not retain access/refresh tokens after login. This cookie identifies the human session; it is not the station's source-enrollment cookie.

Serve the future Next.js management frontend and management backend paths through one HTTPS origin. Keep Next.js as the frontend rather than replacing it with a .NET UI. Use path routing/reverse proxying in the later management-hosting slice; do not assume an arbitrary cross-site API origin can share the session. For the first Development-only proof, use the existing loopback cloud API origin `https://localhost:7100` without introducing the management frontend yet.

Keep named management cookie/OIDC schemes and explicit policies separate from the existing gateway bearer scheme. Do not change gateway ingestion to accept management cookies or allow gateway tokens to authenticate management routes. A human actor is constructed only from a validated interactive management session, never an arbitrary subject/role in a gateway token.

The first proof offers login, safe session-status JSON, and explicit sign-out. It does not grant plant membership merely because login succeeds and does not expose order storage or writes. No application-owned grants or customer master records are implied.

## Security and implementation checks

- Start disabled by default; expose the initial proof only in Development over loopback HTTPS. Existing scan/gateway behavior must remain unchanged when disabled.
- Client credentials belong only on the server. User account password and application-client secret are distinct. Document private setup without reading private environment files or asking for credentials in chat.
- Keycloak client: standard code flow enabled; implicit, password/direct grants, and service accounts disabled. Enforce PKCE S256 and exact callback/post-logout URLs; do not use wildcard redirects or web origins.
- Validate expected issuer/client, signature, lifetime, state, nonce, and PKCE through standard middleware. Bound identity-service requests; no per-scan or gateway-acceptance dependency is introduced.
- API-style session inspection returns 401 without a valid management session, rather than an unexpected login-page redirect. Return only safe status/display data, not tokens, raw claims, issuer/subject identifiers, or client credentials.
- Protect cookie-backed changes, including sign-out and future order writes, against CSRF. Use fixed or validated local return paths to prevent open redirects. Define short, bounded nonpersistent session expiry and reauthentication before testing; do not silently adopt unlimited sliding sessions.
- Distinguish local sign-out from identity-provider single sign-out and explain the behavior during a Keycloak outage. Do not promise immediate revocation of an already-issued session. Production cookie-key persistence, restart behavior, revocation, and broader logout need explicit later evaluation.
- Test disabled/non-Development routes, anonymous access, gateway-token rejection, login/callback protection, secure cookie configuration, expiration, CSRF, logout, and safe failure behavior. A mock test does not prove real Keycloak sign-in; verify a real development login separately.

## Consequences

Implementation: named OIDC/cookie handlers and disabled-by-default loopback HTTPS routes are added to the existing API. The session is nonpersistent and fixed at 15 minutes with no sliding refresh. Logout is CSRF-protected local cookie sign-out, not provider-wide logout or server-side ticket revocation. Fresh login requests use `prompt=login`. No tokens are retained in the cookie ticket, and no order access is enabled. A separate credential-free client import file and hidden-prompt startup helper support manual setup; live Keycloak account/client provisioning and browser login still require the user. See [setup](../MANAGEMENT_SIGN_IN_SETUP.md).

The browser frontend does not need to manage bearer tokens, and authorization stays next to the existing business API. Deployment needs one management HTTPS origin and explicit routing. This adds a framework authentication dependency but no new service/datastore. It leaves production identity hosting and offline station sessions unresolved.

## Accepted display-metadata extension (2026-10-15)

The user approved readable account names for future order audit entries. Request optional `profile` with `openid`; after standard token validation retain only one bounded string `preferred_username` in a backend-owned protected claim. Never trust incoming copies of that claim or preserve unrelated profile/email/role/permission claims. Names are nullable display snapshots, not unique identity, authorization, retry identity or a replacement for stable issuer/subject. Missing/invalid/ambiguous names remain unavailable. The order module snapshots this metadata atomically with new audit evidence; historical audit names are not backfilled or changed by profile updates/retries. The additive column and explicit scope attachment are documented in [history setup](../ORDER_HISTORY_SETUP.md). Existing live clients are not automatically reconfigured, and no new service/protocol/dependency or management permission is introduced.


## References

- [Microsoft: OpenID Connect web authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)
- [Keycloak: securing applications with OpenID Connect](https://www.keycloak.org/securing-apps/oidc-layers)
- [Management identity plan](../MANAGEMENT_IDENTITY_PLAN.md)
