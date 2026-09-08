# Project status

Last updated: 2026-09-08

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

Next checkpoint: authenticated remote access (checkpoint 5). The existing diagnostics/replay routes remain local development tools with an explicitly unattributed audit actor, not production administration. Full workflow authorization, business effects, item resolution, and UI remain pending.

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
