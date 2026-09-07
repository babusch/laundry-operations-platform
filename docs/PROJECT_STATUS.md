# Project status

Last updated: 2026-09-07

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

Verification: all 107 .NET tests pass (62 gateway, 44 cloud, one cross-component synchronization test), as do all 10 Node contract tests. The build has zero warnings/errors. Tests cover acceptance atomicity and boundaries, retry scheduling, permanent rejection, invalid receipts, scoped dispatch, lease expiry/stale completion, and failure of local receipt bookkeeping. The cross-component test uses both real applications and separate PostgreSQL databases, simulates cloud outage and response loss after cloud commit, restarts the gateway, and verifies two events synchronize without duplicate cloud observations. A live localhost smoke test synchronized the earlier pending scan plus one new synthetic scan; both remain in the development databases. The gateway and cloud processes were stopped afterward; both database containers remain running.

Next checkpoint: failure diagnostics, pending/oldest-age visibility, and controlled handling of needs-attention records. Basic permanent-failure preservation is already in place; an administrative replay interface is not. Authenticated remote access must be added before connecting across machines. Full workflow authorization, business effects, item resolution, and UI remain pending.

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
