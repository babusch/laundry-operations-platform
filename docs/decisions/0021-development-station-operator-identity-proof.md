# 0021 — Development station operator identity proof

Status: Accepted for the identity-only development step authorized 2026-10-18.

## Decision

Add a separate confidential `operator-development` OIDC client and named gateway authentication handlers over trusted loopback `https://localhost:7200/operator`. Reuse the existing Keycloak service and the framework's code/PKCE implementation. Add the ASP.NET Core OpenID Connect package matching the existing .NET 10.0.11 dependency baseline. No direct/password grant or custom password verifier is introduced.

Offer explicit login, safe authenticated session status, antiforgery token issuance and CSRF-protected local sign-out. This step has no PWA redesign, permission grant, business command, inactivity lock, unlock or station takeover mechanism. The status is an identity proof, not an active production operator/workflow session.

Use a separately named Secure/HttpOnly/host-only cookie and an in-process server ticket store. Tickets have a fixed **15-minute development-test expiry**, no sliding renewal, no saved access/refresh token. Sign-out removes the server ticket; process restart invalidates it. This is not the approved 15-minute inactivity lock or a production shift lifetime. Memory-only tickets are deliberately not a production session persistence decision.

Require an already enrolled active browser source on login/callback/status. Bind the proof to its tenant, plant, station and source; revalidate that binding on use. Account login alone grants nothing. Do not use management cookies, gateway bearer tokens or claimed roles/permissions as operator proof. Persist only minimal verified issuer/subject, bounded optional display name and server-owned provenance in the ticket.

Keep the feature disabled by default and absent outside Development, with origin/host/loopback guards before callbacks. Use exact authority/client/callback, state/nonce/PKCE validation, bounded HTTPS requests and fixed local redirects. Password entry remains in Keycloak. Do not expose credentials, tokens or claims through status/errors/logs.

Browser compatibility correction (2026-10-22): Keycloak's no-referrer form response can legitimately carry `Origin: null`. Permit that value or the exact provider origin only on the exact form-urlencoded POST callback; neither is an authentication credential. All framework OIDC and source-binding checks still run. Other paths/methods/content types do not inherit this exception, and ordinary cookie-backed commands retain origin/antiforgery restrictions. This is not a CORS or authentication bypass. See [diagnosis and retry instructions](../STATION_OPERATOR_IDENTITY_SETUP.md#browser-callback-compatibility-2026-10-22).

Login needs reachable Keycloak; local status/sign-out use the local ticket store and source database without identity calls. Identity outages are safe errors, not fallback login. Local source revocation invalidates the proof on its next use. Sign-out is local, not provider-wide SSO logout.

## Consequences and remaining work

The in-memory store means a restart requires reauthentication. It does not enforce one operator across distinct browser installations, stop a pending independent login callback after logout, or provide durable login audit/session lifecycle; those belong to the reviewed station-session step before business use. Existing development raw observations remain non-operational and unchanged. Production shift duration, offline unlock and identity hosting remain unresolved. Staff badges remain future work.

Provide a placeholder-only client import, safe startup helper and manual setup instructions. Creating/importing the live client and granting access require explicit setup; no live credential inspection or privilege change is implied by code implementation. Real Keycloak/browser verification is distinct from mocked protocol tests and must be reported honestly.

See [operator-session plan](../STATION_OPERATOR_SESSIONS.md), [management boundary](0019-management-server-owned-login-session.md) and [offline policy](0009-identity-permissions-and-offline-access.md).
