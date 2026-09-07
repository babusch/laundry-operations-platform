# 0005 — Use PostgreSQL for plant storage

Status: Accepted  
Date: 2026-09-07

## Context

The gateway needs durable local storage independent of cloud availability. SQLite would simplify installation, but the planned plant-wide service will combine scan acceptance, delivery bookkeeping, reference data, and local workflows. Pilot throughput has not yet been measured.

## Decision

Use a separate PostgreSQL instance and persistent storage at each plant. The user confirmed this choice after reviewing SQLite's operational advantages and single-writer tradeoff. Reuse the PostgreSQL/EF Core tooling already used by the cloud, but never share databases or persistence entities. See [the gateway plan](../LOCAL_GATEWAY_PLAN.md) for the comparison.

For development, add a separate Compose service and named volume, bound to loopback port 15433. Preserve the cloud database on port 15432. Applications run directly with dotnet. Production packaging remains undecided.

## Consequences

Concurrent local work has PostgreSQL's transaction and concurrency facilities. Each plant also requires database service management, upgrades, credentials, backups, capacity monitoring, and recovery procedures. Cloud synchronization does not replace backup of unsynchronized local evidence.

Checkpoint 1 checks connectivity only and creates no application tables. Introduce explicit migrations when checkpoint 2 adds observations and the outbox; never automatically mutate production schemas on startup.

## Revisit when

Measured pilot workloads or installation/support constraints favor a compact SQLite appliance. Changing engines requires a new decision and migration/testing work, not merely a connection-string edit. Do not implement both engines speculatively.
