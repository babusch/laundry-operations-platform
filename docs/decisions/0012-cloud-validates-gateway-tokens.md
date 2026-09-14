# 0012 — Cloud validates development gateway tokens

Status: Accepted
Date: 2026-09-09

## Context

Keycloak can issue a scoped development gateway token, but the cloud scan endpoint previously trusted only loopback access and server-configured source IDs. The next secure slice must establish cloud-side authentication before the gateway is taught to obtain tokens.

## Decision

Protect the Development scan-ingestion endpoint with ASP.NET Core JWT bearer authentication and a named authorization policy. Validate a signed token's HTTPS authority metadata, issuer, intended `laundry-cloud-api` audience, lifetime, and signature. Require the project-owned `laundry_permissions=scans.ingest` claim. Read tenant and plant registration from verified token claims, reject missing/malformed registration, and compare it with the immutable event body before storage.

Retain the Development-only and loopback-only route boundary for this slice; Staging and Production still do not map the endpoint. Serve the local cloud API over trusted HTTPS when run from its launch profile. Disable gateway forwarding by default until the next slice adds token acquisition and HTTPS delivery. Local acceptance and durable queued evidence remain independent.

Use framework authentication rather than custom token cryptography. Explicitly enable metadata refresh when a token references an unknown signing key. Automated tests use locally signed tokens without depending on a live identity service; separate live smoke tests verify actual Keycloak discovery, delivery, and signing-key rollover. Cross-component synchronization tests use an explicit test-only authentication scheme until the gateway obtains real tokens.

## Consequences

Missing, invalid, expired, wrong-issuer, and wrong-audience tokens return 401. An authenticated identity without permission or valid/matching tenant/plant registration returns 403. Only then can validation and durable ingestion run. Keycloak availability is needed when the cloud first loads or refreshes signing metadata, not for local plant acceptance.

During normal key rotation, Keycloak publishes both the new active key and the previous passive key. A token carrying the new key ID may receive one transient 401 while the cloud requests fresh metadata. The gateway retries that unchanged event once and its durable queue continues later if the refresh has not completed. Metadata refresh adds only keys published by the configured HTTPS authority; it does not allow an unrelated signing key.

This does not complete checkpoint 5.2: the real gateway still does not obtain or attach a token, production-grade asymmetric credentials remain unresolved, and remote production ingestion remains disabled.

References: [ASP.NET Core Minimal API security](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/security), [JWT bearer key-refresh behavior](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.jwtbearer.jwtbeareroptions.refreshonissuerkeynotfound), [Keycloak realm key rotation](https://www.keycloak.org/docs/latest/server_admin/#configuring-realm-keys), and [Keycloak service accounts](https://www.keycloak.org/docs/latest/server_admin/#_service_accounts).

Implementation update: ADR 0013 connects the Development gateway sender to this boundary with short-lived tokens while retaining the loopback-only restriction.

Rollover verification update (2026-09-13): an automated test starts with the previous public key cached, observes the refresh request caused by a newly signed token, accepts that token after refresh, continues accepting the previous passive key, and rejects an unrelated key. A reversible live test repeated the same rollover against Keycloak and an already-running cloud API, then removed the temporary provider and confirmed the original Development key was active again.
