# 0013 — Gateway acquires short-lived development tokens

Status: Accepted  
Date: 2026-09-13

## Context

The cloud Development ingestion route validates Keycloak gateway tokens (ADR 0012), but the gateway could not obtain or attach one. Forwarding therefore had to remain disabled. Token acquisition must not become part of local scan acceptance, and an identity outage must not misclassify valid queued observations as bad events.

## Decision

In Development, use the OAuth 2.0 client-credentials grant against the local Keycloak token endpoint. Supply the private client secret through process configuration, never checked-in application settings. The development start helper resolves the existing ignored Compose setting and passes it only to the gateway child process without printing it.

Keep the access token only in gateway memory. Share one cached token across deliveries, serialize refreshes so concurrent work does not create a token-request storm, and refresh before expiry. Attach it as an HTTPS Bearer token without changing the stored event payload. Disable redirects and proxies for both token acquisition and cloud delivery, retain the loopback-only Development boundary, and bound each operation and response.

If token acquisition is unavailable, times out, is rate-limited, returns rejected credentials, or returns an invalid response, retain the observation as `pending` with a safe diagnostic code. Local acceptance remains independent. If the cloud returns 401, invalidate the cached token, obtain a replacement, and retry that delivery once. Classify the second cloud response using the existing delivery rules.

## Consequences

The development gateway can now synchronize through the real Keycloak and protected cloud boundary. Identity outages consume retry attempts and backoff but do not stop scanning or discard work. Invalid credentials continue retrying after configuration is repaired instead of moving every plant event into manual replay.

This does not approve production shared-secret provisioning, remote endpoints, certificate lifecycle, gateway enrollment, local station authentication, or supervisor identity. Production should prefer a stronger per-installation credential such as a managed asymmetric key where the selected identity provider and deployment model support it.

## Revisit when

Selecting production identity hosting and gateway enrollment, supporting remote Development/Staging plants, rotating credentials, or defining token and certificate storage for the target plant operating system.

Verification update: a database-backed automated test and a live Keycloak stop/restart test prove that identity loss before token acquisition leaves the event pending, preserves the exact payload, and synchronizes automatically after recovery. Token acquisition and cloud delivery use separate bounded time windows so a cold identity request does not consume the cloud request budget.

Disabled-identity update: the same database-backed test now covers rejected credentials, and a live test disabled only the registered gateway service account before token acquisition. The cloud was not called, local acceptance remained available, the event stayed pending with a safe code, and re-enabling the account resumed unchanged delivery. Disabling prevents new token issuance but does not invalidate an already-issued signed token before its five-minute expiry; production revocation objectives may require a different lifetime or an explicit deny-list/introspection tradeoff.
