# Project status

Last updated: 2026-09-09

## Current phase

**Phase 0 — discovery and risk validation**

Initial application scaffolding has begun. The architecture and delivery order are documented, but real workflows, hardware, and deployment constraints still need validation.

The Windows development toolchain has been verified. Repository-level .NET, Node.js, and pnpm versions, the pnpm workspace, formatting rules, and ignore rules are now in place. The root .NET solution, local PostgreSQL Docker Compose service, and first cloud API health endpoint with an integration test are established. The provisional `scan.observed` v1 JSON contract now defines the first barcode/RFID message in the walking skeleton.

## Next outcome

Current checkpoint: `POST /api/scans` validates the shared v1 JSON Schema and durably stores observations. Atomic insertion distinguishes accepted events, unchanged retries, and conflicting IDs. Tenant and plant scope comes from server configuration; the endpoint is restricted to loopback requests in Development. Staging/Production ingestion remains disabled pending gateway authentication (ADR 0004).

Verification complete: all 44 .NET tests pass, covering validation, local-only access, tenant/plant boundaries, concurrent duplicates/conflicts, lost-acknowledgement retries, delayed observations, and database outage/recovery. The initial migration and additive payload-preservation migration are applied to the development database. Docker PostgreSQL uses `localhost:15432` to avoid an existing Windows PostgreSQL service on port 5432. See `docs/DEVELOPMENT_SETUP.md` for repeatable commands.

The user approved PostgreSQL for plant storage ([ADR 0005](decisions/0005-use-postgresql-for-plant-storage.md)). Checkpoints 1–3 of the [local gateway plan](LOCAL_GATEWAY_PLAN.md) are implemented: `Laundry.Edge` runs on localhost:5200, with independent `plant-postgres` storage on port 15433 and its own named volume. Health endpoints distinguish process liveness from plant database connectivity; neither depends on cloud availability.

Gateway `POST /api/scans` validates `submit-scan.v1`, assigns gateway time, and commits immutable observation evidence plus a pending outbox row in one transaction. It is Development/loopback-only and checks a configured synthetic tenant/plant/station/device tuple. Retries retain the original receipt; conflicting IDs return 409. See [ADR 0006](decisions/0006-durable-local-scan-acceptance.md).

The gateway now forwards stored events unchanged to the local cloud API in Development. Durable scoped leases, retry scheduling, and validated receipts update outbox state to `synchronized`; permanent failures become `needsAttention`. Transient outages remain pending without blocking local acceptance. Both gateway migrations (`LocalScanAcceptance`, `OutboxDeliveryTracking`) are applied locally; startup does not mutate schemas. See [ADR 0007](decisions/0007-forward-plant-outbox-to-local-cloud.md).

Two synthetic gateway observations remain synchronized in development storage. Docker recovered after the user started it without rebooting. The plant database is running; the gateway smoke-test process was stopped afterward.

Checkpoint 4 is complete: Development/loopback-only `/api/sync` summary, bounded metadata lists/details, replay history, and idempotent audited replay. The `ReplayAudit` migration is applied to the local plant database. See [ADR 0008](decisions/0008-local-sync-diagnostics-and-audited-replay.md) and the setup guide.

Current verification: all 130 .NET tests pass (85 gateway, 44 cloud, one cross-component scenario). The build succeeds with zero warnings/errors, and the 10 Node contract tests previously passed with unchanged contracts. Tests cover scoped diagnostics, pagination, replay idempotency/concurrency, stale state/live lease rejection, audit/requeue rollback, storage outage/recovery, and replay through the real gateway/cloud path without payload changes or duplicates. An outage test exposed EF's wrapped transient database exception; diagnostics now return a safe 503 for that case. Live summary/list requests returned two synchronized scans, zero pending, and zero needs-attention records without exposing payloads.

Checkpoint 5 security is proceeding in reviewed slices. ADR 0009 remains the broader proposed model; its offline policy is approved. Local Keycloak and a development gateway identity are verified (ADRs 0010–0011). The cloud-side slice is complete (ADR 0012): Development ingestion validates signed tokens and authorization before tenant/plant-scoped storage. The real gateway does not acquire tokens yet, so forwarding is disabled by default and checkpoint 5.2 is not complete. Production identity hosting and asymmetric credentials remain open. Existing diagnostics/replay routes remain local development tools with an unattributed audit actor.

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

Latest identity verification (2026-09-09): Keycloak starts successfully, discovery/public signing keys pass normal trusted HTTPS verification, and the user verified interactive `local-admin` login. The current configured password had differed from the identity database role's persisted password. Aligning that role with the current development setting fixed network authentication without deleting data. Loopback PostgreSQL uses trust and must not be used to prove password correctness; verification used the container-network hostname. Private `.env` stays ignored and untracked. Root, Claude, and Copilot instructions permit scoped development-credential inspection only with explicit user authorization, without publishing or committing values. Cloud-side gateway authentication is now enabled on the Development ingestion route; gateway-side token acquisition remains pending.

Identity setup slice: the user approved product-managed identity and local Keycloak evaluation. The optional HTTPS-only development Compose stack, dedicated identity PostgreSQL volume, setup guide, and discovery smoke-test script are added (ADR 0010). Live startup and trusted HTTPS verification now pass. The cloud validates gateway tokens, but checkpoint 5.2 remains incomplete until the gateway can obtain and use them. Production provider and hosting remain open.

Development gateway identity bootstrap is complete (ADR 0011): the reproducible `laundry-development` realm imported successfully, and an idempotent initializer applies the explicit gateway role scope to existing realms. Live verification proves invalid credentials are rejected and the valid service account receives a five-minute token containing only the `scans.ingest` application permission, the cloud API audience, and fixed synthetic tenant/plant claims. The user's distinct gateway credential remains in ignored private `.env`; neither it nor the token is printed or stored. This local shared-secret proof does not approve production credentials or connect application authentication.

Cloud gateway authorization slice complete (ADR 0012): ASP.NET Core validates signature, issuer, audience, expiry, `scans.ingest`, and token-derived tenant/plant before ingestion. Automated coverage is now 54 cloud tests, 85 gateway tests, and one cross-component test (140 .NET total), with zero build warnings. Live Keycloak-to-cloud HTTPS verification returned 401 without credentials, 403 for a mismatched plant, 201 for a permitted new scan, and 200 with the original receipt on unchanged retry. One synthetic observation was added to local cloud development storage. The cloud test process was stopped; identity services and cloud PostgreSQL remain running. Gateway forwarding defaults off until token acquisition is implemented.

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
