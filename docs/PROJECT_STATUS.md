# Project status

Last updated: 2026-09-07

## Current phase

**Phase 0 — discovery and risk validation**

Initial application scaffolding has begun. The architecture and delivery order are documented, but real workflows, hardware, and deployment constraints still need validation.

The Windows development toolchain has been verified. Repository-level .NET, Node.js, and pnpm versions, the pnpm workspace, formatting rules, and ignore rules are now in place. The root .NET solution, local PostgreSQL Docker Compose service, and first cloud API health endpoint with an integration test are established. The provisional `scan.observed` v1 JSON contract now defines the first barcode/RFID message in the walking skeleton.

## Next outcome

Current setup checkpoint: the API has an EF Core/Npgsql connection, an explicit initial migration for `integrations.scan_observations`, and a database readiness endpoint. Scan storage has a globally unique event ID primary key and separate source, gateway, and cloud timestamps. Database tests use isolated PostgreSQL containers.

Verification complete: Docker Desktop is running, the initial migration is applied to the development database, and all nine .NET tests pass (eight database cases plus process health). The running API's `/health/ready` returned HTTP 200 with `Healthy`. The Docker database uses `localhost:15432` to avoid an existing Windows PostgreSQL service on port 5432. See `docs/DEVELOPMENT_SETUP.md` for repeatable commands.

Next implementation checkpoint: add the scan ingestion HTTP endpoint with contract validation, trusted tenant/plant scoping, and explicit duplicate/conflicting-payload handling. The current storage context is internal infrastructure; no scan read/write endpoints or tenant authorization are exposed yet. Database uniqueness alone is not complete ingestion idempotency.

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
