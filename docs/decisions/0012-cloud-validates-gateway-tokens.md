# 0012 — Cloud validates development gateway tokens

Status: Accepted
Date: 2026-09-09

## Context

Keycloak can issue a scoped development gateway token, but the cloud scan endpoint previously trusted only loopback access and server-configured source IDs. The next secure slice must establish cloud-side authentication before the gateway is taught to obtain tokens.

## Decision

Protect the Development scan-ingestion endpoint with ASP.NET Core JWT bearer authentication and a named authorization policy. Validate a signed token's HTTPS authority metadata, issuer, intended `laundry-cloud-api` audience, lifetime, and signature. Require the project-owned `laundry_permissions=scans.ingest` claim. Read tenant and plant registration from verified token claims, reject missing/malformed registration, and compare it with the immutable event body before storage.

Retain the Development-only and loopback-only route boundary for this slice; Staging and Production still do not map the endpoint. Serve the local cloud API over trusted HTTPS when run from its launch profile. Disable gateway forwarding by default until the next slice adds token acquisition and HTTPS delivery. Local acceptance and durable queued evidence remain independent.

Use framework authentication rather than custom token cryptography. Automated tests use locally signed tokens without depending on a live identity service; a separate live smoke test verifies actual Keycloak discovery and delivery. Cross-component synchronization tests use an explicit test-only authentication scheme until the gateway obtains real tokens.

## Consequences

Missing, invalid, expired, wrong-issuer, and wrong-audience tokens return 401. An authenticated identity without permission or valid/matching tenant/plant registration returns 403. Only then can validation and durable ingestion run. Keycloak availability is needed when the cloud first loads or refreshes signing metadata, not for local plant acceptance.

This does not complete checkpoint 5.2: the real gateway still does not obtain or attach a token, production-grade asymmetric credentials remain unresolved, and remote production ingestion remains disabled.

References: [ASP.NET Core Minimal API security](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/security), [JWT bearer validation](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication), and [Keycloak service accounts](https://www.keycloak.org/docs/latest/server_admin/#_service_accounts).
