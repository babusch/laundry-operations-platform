# 0006 — Durably accept scan submissions with a plant outbox

Status: Accepted  
Date: 2026-09-07

## Context

A station cannot supply a gateway acceptance time. The gateway must acknowledge scans without cloud availability, survive restarts, and preserve an unchanged cloud event for later forwarding. Local evidence and its delivery obligation must never be committed separately.

## Decision

- Add `submit-scan.v1` under shared request contracts. It carries the existing observation fields except `gatewayAcceptedAtUtc`. `eventType` still identifies the resulting `scan.observed` event. The existing cloud event contract is unchanged.
- Expose gateway `POST /api/scans` only in Development when enabled, restricted to loopback. One configured synthetic tenant/plant/station/device tuple is permitted. This is simulator scoping, not authenticated identity; add real authorization before remote access.
- Validate the embedded schema, reject duplicate JSON properties, bound bodies to 16 KiB and depth eight, and enforce the cloud adapter's date/NUL representation limits before local acceptance.
- Store immutable submission JSON and accepted-event JSON as text in `plant.observations`, alongside indexed tenant/plant, event ID, and gateway time. Preserve original strings, including source-clock precision. Share contracts, not persistence entities.
- Assign gateway time while preparing the transaction, rounded to PostgreSQL microseconds. It is an acceptance timestamp, not an exact measurement of the commit instant; expose it only after commit. Retries return the original value.
- In one explicit PostgreSQL transaction, atomically insert the observation by unique event ID and insert its `plant.outbox` row with status `pending`. A foreign key relates delivery bookkeeping to immutable evidence. Read the stored row within tenant/plant scope and compare original submission values before acknowledging.
- JSON whitespace and property order do not affect retries. Changed values with the same ID return 409 without overwriting or revealing another scope's evidence. Unchanged retries return 200 and the original receipt; new acceptance returns 201. Both identify local acceptance and pending delivery, not cloud synchronization or business approval.
- Storage failure returns 503 with a retry hint. A lost response or uncertain commit means retry the exact same submission, not generate a new ID. Log event/correlation IDs without raw identifiers.
- Apply migrations explicitly. No automatic startup migration, cloud connection, delivery worker, item resolution, or business mutation is added in this checkpoint.

## Consequences

Pending scans survive application/database restarts and do not depend on cloud connectivity. The outbox references the immutable accepted event instead of duplicating its payload. All entries remain pending until checkpoint 3 implements forwarding. Future delivery state and attempt fields require their own migration as needed.

Append-only evidence is enforced by this application's insertion-only path; database administrator access is not an immutable storage guarantee. Production database permissions, backups, disk/power durability, authentication, reconciliation, and retention still require hardening. Conflict requests are rejected, not stored as a new operational observation; a future reconciliation audit is separate.

Tests cover accepted barcode/RFID events against the cloud schema, retries, concurrent collisions, source boundaries, local-only access, delayed/wrong-clock input, injected outbox failure and rollback, database outage/recovery, and application restart. These are simulated failures, not power-cut certification.

## Revisit when

Adding real devices/multiple stations, authenticated identity, reference data, delivery workers, or operational business effects changes this minimal acceptance boundary.
