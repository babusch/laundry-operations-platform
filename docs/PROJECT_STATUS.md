# Project status

Last updated: 2026-09-07

## Current phase

**Phase 0 — discovery and risk validation**

Initial application scaffolding has begun. The architecture and delivery order are documented, but real workflows, hardware, and deployment constraints still need validation.

The Windows development toolchain has been verified. Repository-level .NET, Node.js, and pnpm versions, the pnpm workspace, formatting rules, and ignore rules are now in place. The root .NET solution, local PostgreSQL Docker Compose service, and first cloud API health endpoint with an integration test are established. The provisional `scan.observed` v1 JSON contract now defines the first barcode/RFID message in the walking skeleton.

## Next outcome

Current checkpoint: `POST /api/scans` validates the shared v1 JSON Schema and durably stores observations. Atomic insertion distinguishes accepted events, unchanged retries, and conflicting IDs. Tenant and plant scope comes from server configuration; the endpoint is restricted to loopback requests in Development. Staging/Production ingestion remains disabled pending gateway authentication (ADR 0004).

Verification complete: all 44 .NET tests pass, covering validation, local-only access, tenant/plant boundaries, concurrent duplicates/conflicts, lost-acknowledgement retries, delayed observations, and database outage/recovery. The initial migration and additive payload-preservation migration are applied to the development database. Docker PostgreSQL uses `localhost:15432` to avoid an existing Windows PostgreSQL service on port 5432. See `docs/DEVELOPMENT_SETUP.md` for repeatable commands.

The user approved PostgreSQL for plant storage ([ADR 0005](decisions/0005-use-postgresql-for-plant-storage.md)). Checkpoint 1 of the [local gateway plan](LOCAL_GATEWAY_PLAN.md) is implemented: `Laundry.Edge` runs on localhost:5200, with independent `plant-postgres` storage on port 15433 and its own named volume. Health endpoints distinguish process liveness from plant database connectivity; neither depends on cloud availability. No gateway application tables or migrations are needed yet, and startup does not mutate schemas.

Gateway verification: all four new tests pass, including database outage/recovery without a cloud service, liveness without configuration, missing-configuration readiness, and absence of a scan endpoint. Together with the 44 cloud tests, all 48 .NET tests pass. The build has zero warnings/errors; Compose configuration validation and real local gateway health requests passed.

Next checkpoint: define the station-to-gateway submission contract and add durable local acceptance with a transactional outbox. Forwarding follows separately. Authenticated remote access must be added before connecting across machines. Full workflow authorization, business effects, item resolution, and UI remain pending.

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
