# 0007 — Forward the plant outbox to the local development cloud

Status: Accepted  
Date: 2026-09-07

## Context

Locally accepted scans need eventual cloud delivery without holding up acceptance. Connection failures and lost acknowledgements make duplicate delivery unavoidable. The existing cloud boundary is intentionally Development/loopback-only until authenticated gateway identity is implemented.

## Decision

- Run a .NET hosted worker inside the gateway only when the environment is Development and `Forwarding:Enabled` is true. The checked-in Development default enables it. Require a literal loopback IP HTTP endpoint with path `/api/scans`; disallow credentials, query strings, fragments, proxies, and redirects. No remote or production delivery is authorized by this checkpoint.
- Select pending work only for the configured tenant and plant. Order by gateway acceptance time and event ID, not source time; this is delivery scheduling, not physical inventory conflict resolution.
- Use a short `FOR UPDATE SKIP LOCKED` transaction to claim one row, increment its attempt count, and persist a random lease token with a 30-second expiry. Release database locks before network I/O. There is no separate broker or database connection to the cloud.
- Send the immutable stored event JSON unchanged. Bound the request/response operation to five seconds and receipts to 4 KiB. The cloud must return the matching event ID, expected status (`accepted`/201 or `alreadyProcessed`/200), and a valid non-default UTC receipt timestamp before the outbox becomes `synchronized`.
- Persist `pending`, `synchronized`, or `needsAttention` in delivery bookkeeping, plus attempts, next attempt time, cloud receipt time, safe error code, and lease fields. Preserve observations and synchronized outbox rows; no retention/delete policy is implemented.
- A conditional update using the lease token prevents a stale sender from overwriting a newer attempt. Crashes, cancellation, or failed receipt bookkeeping leave a lease that expires, permitting unchanged replay. No exactly-once transport claim is made.
- Connection failures, timeouts, invalid receipts, HTTP 408/429, and 5xx remain pending. Retry after exponential delays starting around 4–5 seconds and capped at 4–5 minutes, with jitter. Respect Retry-After delta/date hints up to a one-day safety cap. Persist scheduling across restarts; there is no attempt limit for transient outages.
- Other responses, including permanent 4xx and redirects, become `needsAttention` rather than retrying endlessly. Store only safe error codes such as `http_409`, never remote response bodies. A bad configuration or permission failure can require intervention; it is not synchronization success.
- Poll idle/error loops every two seconds. Database errors do not stop local acceptance's host, but readiness continues to report plant connectivity, not forwarding health. Apply migrations explicitly before running the new binary.

## Consequences

The plant can keep accepting scans while cloud delivery is unavailable. A successful local receipt does not wait for cloud delivery. Retrying a submission can report its current delivery status, while preserving its original gateway timestamp.

This is an initial sequential sender, not a high-throughput multi-plant scheduler. Lease timing depends on the plant clock; serious clock changes can delay retries or permit duplicate sends, which cloud idempotency must tolerate. Pending capacity monitoring, health dashboards, authenticated remote access, safe administrative replay of needs-attention records, compatibility windows, and recovery operations remain later checkpoints. Do not repair immutable payloads or manually generate replacement event IDs to bypass a conflict.

Tests use real PostgreSQL for leases, rollback, and delivery state. A cross-component test uses both real HTTP applications with separate databases and a test-only transport adapter to simulate cloud outage and acknowledgement loss after commit, followed by gateway restart. This is not real-device or power-cut testing.

## Revisit when

Authenticated networking, throughput measurements, supported upgrades, or operational diagnostics require extending this deliberately local-development delivery boundary.
