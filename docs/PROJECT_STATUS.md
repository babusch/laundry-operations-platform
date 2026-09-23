# Project status

Last updated: 2026-09-23

## Current phase

**Phase 0 — discovery and risk validation**

Initial application scaffolding has begun. The architecture and delivery order are documented, but real workflows, hardware, and deployment constraints still need validation.

The Windows development toolchain has been verified. Repository-level .NET, Node.js, and pnpm versions, the pnpm workspace, formatting rules, and ignore rules are now in place. The root .NET solution, local PostgreSQL Docker Compose services, and cloud/gateway health endpoints are established. `submit-scan.v2` and `scan.observed.v2` now define the current raw barcode/RFID path; v1 is frozen for historical compatibility only.

## Next outcome

Current checkpoint: the Development gateway-to-cloud path is authenticated end to end, including signing-key rollover. The gateway obtains and caches a short-lived Keycloak token, sends its immutable outbox event over local HTTPS, and the cloud validates the token and token-derived tenant/plant scope before durable ingestion. Both application routes remain loopback-only in Development; Staging and Production ingestion remain disabled.

Cloud verification includes 63 tests covering v1/v2 validation and persistence, authentication, signing-key rollover, local-only access, tenant/plant boundaries, concurrent duplicates/conflicts, lost-acknowledgement retries, delayed observations, and database outage/recovery. The additive trusted-source-v2 migration is applied to the development database. Docker PostgreSQL uses `localhost:15432` to avoid an existing Windows PostgreSQL service on port 5432. See `docs/DEVELOPMENT_SETUP.md` for repeatable commands.

The user approved PostgreSQL for plant storage ([ADR 0005](decisions/0005-use-postgresql-for-plant-storage.md)). Checkpoints 1–4 of the [local gateway plan](LOCAL_GATEWAY_PLAN.md) are implemented. `Laundry.Edge` now uses trusted `https://localhost:7200` as its primary Development address, with `http://localhost:5200` retained only as a temporary loopback transition path. Its independent `plant-postgres` storage uses port 15433 and its own named volume. Health endpoints distinguish process liveness from plant database connectivity; neither depends on cloud availability.

Gateway `POST /api/scans` now validates only `submit-scan.v2`, then adds trusted tenant, plant, station, source, and gateway-time attribution to create `scan.observed.v2`. It commits immutable observation evidence plus a pending outbox row in one transaction. The endpoint remains Development/loopback-only and requires trusted HTTPS, an enrolled browser source, `scans.submit`, and antiforgery. Same-source unchanged retries retain the original receipt; reuse by another source or with changed payload returns 409. Device identity is not required. See [ADRs 0015](decisions/0015-minimal-trusted-scan-v2-contract.md)–[0017](decisions/0017-activate-trusted-scan-v2.md).

The gateway now forwards stored events unchanged to the protected local cloud API in Development using a cached short-lived Keycloak token. Durable scoped leases, retry scheduling, and validated receipts update outbox state to `synchronized`; permanent event failures become `needsAttention`. Cloud or identity outages remain pending without blocking local acceptance. The acceptance, delivery-tracking, replay-audit, and trusted-source-security migrations are applied locally; startup does not mutate schemas. See [ADRs 0007](decisions/0007-forward-plant-outbox-to-local-cloud.md) and [0013](decisions/0013-gateway-acquires-short-lived-tokens.md).

Development storage retains prior synchronized synthetic observations and one additional pending observation accepted during the HTTPS proof.

Checkpoint 4 is complete: Development/loopback-only `/api/sync` summary, bounded metadata lists/details, replay history, and idempotent audited replay. The `ReplayAudit` migration is applied to the local plant database. See [ADR 0008](decisions/0008-local-sync-diagnostics-and-audited-replay.md) and the setup guide.

Current verification: all 196 .NET tests pass (132 gateway, 63 cloud, one cross-component scenario), all 20 Node contract tests pass, and all 18 station tests pass. Station coverage includes English/Swedish selection and fallback, translation-key parity, browser preference persistence, the v2 barcode request, antiforgery submission, durable-result feedback, definite rejection, and exact-event retry after an uncertain result. Gateway coverage includes PWA shell routing, immutable asset caching, and strict separation from API-like routes. The cross-component test sends a v2 station request, preserves the same enrolled source through a gateway restart, and converges through cloud outage and lost acknowledgement without duplicates. Strict v2 request/event schemas and synthetic barcode/RFID examples are verified alongside historical v1 cloud compatibility. The solution builds with zero warnings/errors. A changed-files formatter check passes; the repository-wide formatter still reports pre-existing line-ending/encoding issues in older migrations and unrelated files.

Checkpoint 5 security is proceeding in reviewed slices. ADR 0009 remains the broader proposed model; its offline policy is approved. Local Keycloak, development gateway identity, cloud validation, gateway token acquisition, protected local scan acceptance, and its resilience behavior are verified (ADRs 0010–0014). Checkpoints 5.4.1–5.4.5 are complete; the real-adapter proof remains deferred until hardware is available. ADRs 0015–0017 define and activate the trusted v2 boundary. Historical/queued v1 events remain immutable and cloud-readable until their delivery, replay, and retention obligations end. Authenticated operator/workflow context remains required before scans count as laundry business actions and may produce a later event version or a separate linked business-action event.

The first eight reviewed station-application steps are complete. `apps/station-pwa` is a React/Vite/TypeScript Development simulator shell that reports gateway readiness as checking, ready, or unavailable and offers a manual retry after failure. After readiness succeeds, it uses the same-origin source-session API to report whether this browser is enrolled, needs setup, or could not be checked. From the gateway-hosted HTTPS origin only, an unenrolled browser can invoke the existing Development flow: create a short-lived single-use enrollment, immediately exchange it for a secure `HttpOnly` cookie, and verify the resulting session. The PWA does not display or retain the source ID or enrollment material, and enrolled source identity is not presented as operator authentication. Its approved fixed dark theme uses deep blue-green laundry surfaces, muted light text, and restrained aqua/success/warning/error signals documented in `docs/UI_DESIGN.md`; increased-contrast mode remains dark and restores maximum contrast. English and Swedish resources are bundled for offline use; a native selector remembers the browser preference, updates the document language, and falls back to English. The PWA builds into the gateway release, which serves the application, `/api`, and `/health` from one HTTPS origin; Vite retains a visual-development mode using the same paths through a local proxy ([ADR 0018](decisions/0018-gateway-hosts-station-pwa.md)). API-like paths never fall back to PWA HTML, the shell is revalidated, and fingerprinted assets use immutable caching. "Gateway ready" remains deliberately limited to local plant-storage connectivity and does not imply cloud connectivity or scan acceptance. An enrolled browser can now send a synthetic barcode through the real protected v2 endpoint. The PWA fetches a fresh antiforgery token, creates one immutable request, reports success only from a durable local receipt, and preserves that same request for an idempotent retry when the commit result is uncertain. It clearly labels this as raw observation evidence rather than a laundry operation. It includes no operator sign-in, service worker, browser offline queue, or workflow UI. The next reviewed slice is an RFID-oriented simulator mode built on the same submission boundary.

The pilot laundry is predominantly RFID-based. The approved production direction is therefore an RFID-oriented station workspace with unmistakable active order/customer context, session and order/customer totals, article-type breakdowns, and explicit audited manual-addition and deviation actions. Manual work must remain operator-attributed business activity rather than fabricated scan events; exact production workflow behavior is deferred until it is reviewed with the user and pilot plant.

Produce an executable walking skeleton in which a simulated scan travels through the operator application and local gateway to the cloud API and becomes visible in an audit view.

Before that implementation begins, confirm:

- Initial pilot plant and its exact workflow.
- Whether stock is individually tagged, counted in bulk, measured by weight, or a mixture.
- First barcode and RFID device models.
- Required label printers and machine protocols.
- Target cloud provider and identity provider.
- Languages required on the shop floor.
- Regulatory, hygiene, retention, and data-residency constraints.

## Active architectural baseline

Latest identity verification (2026-09-13): Keycloak starts successfully, discovery/public signing keys and scoped client-credentials issuance pass over trusted HTTPS, and the gateway uses the token against the protected cloud route. Private `.env` stays ignored and untracked. Agents may let normal authorized application/Compose execution consume it internally, but may inspect values only for a specifically authorized development-credential investigation and must never publish or commit them.

Identity setup slice: the user approved product-managed identity and local Keycloak evaluation. The optional HTTPS-only development Compose stack, dedicated identity PostgreSQL volume, setup guide, and verification scripts are added (ADR 0010). Live startup and trusted HTTPS verification pass. The gateway now obtains and uses tokens, while production provider and hosting remain open.

Development gateway identity bootstrap is complete (ADR 0011): the reproducible `laundry-development` realm imported successfully, and an idempotent initializer applies the explicit gateway role scope to existing realms. Live verification proves invalid credentials are rejected and the valid service account receives a five-minute token containing only the `scans.ingest` application permission, the cloud API audience, and fixed synthetic tenant/plant claims. The user's distinct gateway credential remains in ignored private `.env`; neither it nor the token is printed or stored. This local shared-secret proof does not approve production credentials or connect application authentication.

Gateway token acquisition and initial resilience slice complete (ADR 0013): a singleton provider obtains client-credentials tokens, caches them only in memory, serializes refresh, refreshes before expiry, and retries a cloud 401 once with a replacement. A live outage test stopped Keycloak before a fresh gateway requested a token: the gateway stayed ready, accepted a synthetic scan durably, recorded `identity_connection_failed`, and synchronized the unchanged event automatically after Keycloak restarted. Token acquisition and each cloud attempt now have independent five-second bounds. Application processes were stopped afterward; the four local dependency containers remain running. No credential, token, tag value, or generated identifier was printed.

Disabled-gateway resilience is verified: with a fresh gateway holding no cached token, the exact `service-account-gateway-development` account was disabled. Local readiness and durable scan acceptance continued, token issuance returned 401, and the event remained pending with `identity_credentials_rejected`; no cloud delivery occurred. Re-enabling the account synchronized the unchanged event automatically, and the normal identity verification passed afterward. Already-issued signed tokens remain usable until their current five-minute expiry, so disabling an account is bounded revocation rather than instantaneous revocation.

Signing-key rollover is verified without replacing or exposing private keys. An automated cloud test proves that unknown-key metadata refresh accepts the new authority-published key, retains the previous overlap key, and rejects an unrelated key. A live helper temporarily added a higher-priority Keycloak RSA provider while the cloud held cached metadata, delivered synthetic observations under both keys, removed only that provider in cleanup, and confirmed the original development key was active again. The cloud application was stopped after the test; the four dependency containers remain running.

Approved station requirement (2026-09-08): stable source identity with configurable operational purpose (dedicated, flexible, or default-with-switching). Fixed station roles are optional. See DOMAIN.md and PRODUCT.md. This is documented only; the raw scan contract and application behavior are unchanged.

- Repository: one monorepo.
- Cloud: ASP.NET Core modular monolith, worker, PostgreSQL, object storage, and Redis only when justified.
- Management UI: Next.js with TypeScript.
- Shop floor: installable React/Vite PWA with IndexedDB emergency queue.
- Plant: local .NET gateway and local PostgreSQL.
- Integration: keyboard-wedge scanners may feed the PWA; fixed RFID and industrial devices connect through the gateway.
- Synchronization: append-only, versioned, idempotent events with outbox/inbox processing.
- UI design: all user-facing design and review work follows the repository `apple-design` skill together with `docs/UI_DESIGN.md`.

## Update rules

Update this file when the active phase, next outcome, significant blocker, or architectural baseline changes. Do not use it as a historical log; permanent reasoning belongs in an ADR.
