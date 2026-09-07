# Local gateway implementation plan

Date: 2026-09-07  
Status: Overall direction and PostgreSQL storage approved by the user. Checkpoints 1–3 are implemented; checkpoints 4–5 remain planned. Storage reasoning is recorded in [ADR 0005](decisions/0005-use-postgresql-for-plant-storage.md), acceptance in [ADR 0006](decisions/0006-durable-local-scan-acceptance.md), and local forwarding in [ADR 0007](decisions/0007-forward-plant-outbox-to-local-cloud.md).

## Purpose and boundaries

The gateway is a continuously running .NET backend on a managed plant computer, not a PWA or a service installed on every station. It durably accepts local observations and synchronizes them without requiring internet availability. Operator stations use its local API; fixed hardware connects through vendor adapters.

This plan refines the walking skeleton in [ROADMAP.md](ROADMAP.md). Minimal durable acceptance and forwarding are necessary foundations now; full offline business workflows, reference-data synchronization, and production resilience remain later work. Follow [ARCHITECTURE.md](ARCHITECTURE.md), [OFFLINE_AND_SYNC.md](OFFLINE_AND_SYNC.md), and ADRs [0003](decisions/0003-use-a-local-first-plant-gateway.md) and [0004](decisions/0004-bootstrap-local-scan-ingestion.md).

During development, run cloud and gateway applications on the same PC with separate PostgreSQL containers, databases, credentials/configuration, and persistent volumes. The gateway must never access the cloud database directly. Start the gateway with `dotnet run`; determine production service/container packaging after validating the plant OS and hardware SDKs. Docker Desktop is development tooling, not a decided plant deployment requirement.

## Proposed stack

| Concern | Proposal |
|---|---|
| Gateway host and API | C# / .NET 10, ASP.NET Core |
| Background forwarding | A hosted `BackgroundService` inside the gateway process |
| Plant storage | PostgreSQL, separate from cloud storage; see comparison below |
| Persistence | EF Core, Npgsql, explicit migrations |
| Message validation | Shared JSON Schema, JsonSchema.Net |
| Transport | Local HTTP for loopback development; authenticated HTTPS before remote access |
| Tests | xUnit, Testcontainers, synthetic observations |
| Development dependencies | Root Docker Compose orchestration |
| Diagnostics | Correlation-aware logs, health checks, delivery status; no sensitive payload logging |

No separate message broker, Redis, MQTT, Kubernetes, or worker deployment is needed for this checkpoint. Generate future browser API clients from OpenAPI. Do not share persistence entities between applications.

## Scan acceptance and delivery

1. A station or simulator generates an origin event ID, correlation ID, source time, and identifier. Retries preserve the original request.
2. The gateway validates the request and checks trusted tenant/plant/station/device scope. Body fields do not establish authorization. Initial synthetic access stays loopback-only in Development; real access requires authentication.
3. The gateway creates the accepted event and saves it together with an outbox entry in one database transaction. The outbox is a durable list of pending deliveries, not an in-memory queue.
4. Only after commit does the gateway return local acceptance. Cloud availability is not consulted for this acknowledgement.
5. The background worker forwards the stored event to the cloud API. It never regenerates event IDs, timestamps, or payloads on retry.
6. A confirmed cloud receipt updates delivery state. Lost responses result in safe retries, including when the cloud committed before the connection failed.

Accepted means the raw observation was recorded, not that a movement, production transition, or charge was approved. Preserve observations as append-only evidence; keep mutable delivery bookkeeping separate.

### Submission versus accepted-event contract

The implemented `packages/contracts/requests/submit-scan.v1.schema.json` excludes `gatewayAcceptedAtUtc`, which the origin cannot know. The gateway assigns that value when preparing durable acceptance and produces the existing `scan.observed.v1` event. Unchanged retries return the original receipt and acceptance timestamp. Conflicting reuse of an ID is rejected without overwriting history.

### Failure behavior

| Failure | Required result |
|---|---|
| Internet/cloud outage | Continue local acceptance while storage capacity permits; retain pending deliveries. |
| Response lost after cloud commit | Retry the same event; cloud idempotency prevents duplication. |
| Gateway restart | Recover pending deliveries from the database. |
| Duplicate submission | Return the original receipt; no extra observation or outbox entry. |
| Same ID, different content | Reject the conflict; preserve original data. |
| Local database outage or full disk | Do not acknowledge plant acceptance. A failed response may have an uncertain commit outcome, so retry unchanged. |
| Permanent cloud rejection | Preserve the event in an inspectable needs-attention state. |
| Station cannot reach gateway | Future PWA emergency queue uses a distinct device-local status, not plant acceptance. |

Use bounded exponential backoff with jitter for transient failures, respecting server retry hints. Do not discard events merely because an outage lasts a long time. Authentication/configuration failures require diagnostics and intervention, not misleading success. Delayed observations and wrong device clocks must not be silently rewritten or used for last-write-wins inventory updates.

WAN resilience does not solve plant computer, power, or disk failure. Production requires capacity monitoring, backups and restore drills, suitable storage, and a power-loss strategy. Full offline production also requires scoped reference data and local business rules.

## Proposed repository placement

```text
apps/edge/
  Laundry.Edge/
    Scans/
    Persistence/
    Synchronization/
    Health/
  Laundry.Edge.Tests/
packages/contracts/
  events/
  requests/
```

Add migrations under gateway persistence when schema changes are introduced. Add hardware adapter and simulator projects only when needed. Keep orchestration at the root and cross-component resilience tests in the locations described by [REPOSITORY_STRUCTURE.md](REPOSITORY_STRUCTURE.md).

## Small implementation checkpoints

### 1. Gateway foundation — implemented

- Create the host, configuration, separate plant database service/volume, and health checks.
- Document startup and shutdown with beginner-friendly explanations.
- Test process liveness and local database readiness independently of cloud connectivity.
- Demonstrate that stopping the cloud leaves gateway readiness healthy, while losing the plant database makes readiness unhealthy.
- Do not add scan ingestion, forwarding, hardware adapters, or UI yet.

### 2. Durable local acceptance — implemented

- Define and test the submission contract.
- Add explicit migrations for observations and outbox storage.
- Save both atomically; test unchanged retries, concurrent conflicts, and tenant/plant boundaries.
- Demonstrate local acceptance with cloud stopped and persistence after restart.

### 3. Cloud forwarding — implemented

- Add background delivery, retry scheduling, and confirmed delivery bookkeeping.
- Remain on one PC under the existing cloud Development boundary.
- Demonstrate cloud recovery draining pending events without duplicates.

### 4. Failure handling and diagnostics

- Extend tests for lost acknowledgements, restarts, outages, delayed events, wrong clocks, and concurrent delivery.
- Expose pending count, oldest pending age, and safe failure diagnostics.
- Preserve permanently rejected events for inspection and controlled replay; never edit accepted event history to force a retry.
- Distinguish pending, synchronized, and needs-attention delivery states.

### 5. Secure network access

- Add authenticated gateway-to-cloud identity and tenant/plant authorization.
- Secure station-to-gateway access and local HTTPS before connecting separate machines.
- Do not weaken ADR 0004's development-only boundary to bypass authentication.
- Follow with operator PWA integration and selected hardware, as separately scoped checkpoints.

Implement and review one checkpoint at a time. These steps do not complete all roadmap phases or authorize production rollout.

## PostgreSQL versus SQLite

Recommendation for the currently planned plant-wide gateway: retain PostgreSQL. This is a workload and operational tradeoff, not a claim that SQLite is unsuitable for professional systems. There is no measured pilot workload yet, so neither engine has been proven necessary by a benchmark.

SQLite is a credible alternative for a compact, single-process store-and-forward appliance. It embeds in the application and removes the need to install, secure, upgrade, and monitor a separate database service. Multiple stations can still call the gateway API; they must not open a shared database file themselves. SQLite WAL permits readers alongside a writer, but only one write transaction at a time per database. Short transactions and deliberate write scheduling can make that sufficient; station count alone does not determine suitability. See SQLite's [appropriate uses](https://www.sqlite.org/whentouse.html) and [WAL documentation](https://www.sqlite.org/wal.html).

PostgreSQL provides more flexibility for concurrent writes from acceptance, outbox bookkeeping, and future local workflow/reference-data updates. It also reuses our existing database tooling and operational knowledge. This comes with a real cost: another service at every plant, credentials, upgrades, resource usage, and backup management. See PostgreSQL's [concurrency model](https://www.postgresql.org/docs/current/mvcc-intro.html).

Both choices can support transactional acceptance and an outbox. Both need tested durability settings and reliable local storage. If selecting SQLite, use local disk, evaluate WAL with `synchronous=FULL` for power-loss durability, configure contention handling, and test checkpointing, backup/restore, and burst load. WAL with `synchronous=NORMAL` can lose recent committed transactions after power failure. See [SQLite synchronous settings](https://www.sqlite.org/pragma.html#pragma_synchronous). Do not treat copying an active database file as a complete backup procedure.

Before pilot rollout, validate peak accepted observations (including RFID bursts), simultaneous background writes, acceptable acknowledgement latency, outage retention, hardware resources, and who maintains each installation. Consider SQLite if a minimal appliance installation is the dominant constraint and a representative load/recovery test supports it. Record any selected storage change in an ADR and update canonical architecture; do not implement both providers or assume migration will be a connection-string change. Cloud PostgreSQL need not change if edge storage changes.
