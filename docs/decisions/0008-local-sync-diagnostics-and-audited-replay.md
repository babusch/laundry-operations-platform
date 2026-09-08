# 0008 — Local synchronization diagnostics and audited replay

Status: Accepted  
Date: 2026-09-08

## Context

The sender preserves pending and rejected events, but developers need to inspect queue state without reading scan payloads or editing database rows. An explicit replay is an administrative action and must preserve evidence, reject stale decisions, and be safe to retry after response loss.

## Decision

- Add Development-only, loopback-only `/api/sync` endpoints when both `ScanAcceptance:Enabled` and `SyncDiagnostics:Enabled` are enabled. All queries and commands use the configured tenant/plant scope. Other-scope event IDs are indistinguishable from missing IDs. This is not production authorization.
- Expose summary counts, oldest pending age from gateway acceptance time, latest confirmed cloud receipt time, configured forwarding enablement, and a process-local worker iteration heartbeat/error code. Use a repeatable-read database snapshot for the summary. A storage outage returns 503, not a misleading empty/healthy queue. Heartbeats are not live cloud probes and reset at process restart.
- Expose bounded, offset-paginated event metadata and per-event replay history. Never include identifiers, scan payloads, connection strings, or remote response bodies. Offset pages can shift during concurrent changes; this is a diagnostic view, not a consistent export. Suppress HTTP caching.
- Require replay JSON with exactly `requestId` (nonempty UUID), `expectedAttempts` (nonnegative integer), and a reason code: `configurationCorrected`, `cloudIssueResolved`, or `reviewedForRetry`. Limit the body to 2 KiB/depth two. Reject extra/duplicate fields and unsupported values.
- Lock the scoped outbox row. Requeue only `needsAttention` at the reviewed attempt count and without a live lease. Do not requeue ordinary pending/synchronized records or reset cumulative attempts. A replay is a request to deliver again, not conflict resolution or guaranteed synchronization.
- Insert an append-only `plant.replay_audit` record and set the outbox to pending/due-now in the same transaction. Retain the previous attempt count/error, reason code, server time, and original event ID. Never change submission JSON, accepted-event JSON, or gateway acceptance time.
- Use the globally unique replay request ID for idempotency. An identical repeat returns its original replay receipt without changing current delivery state, even after synchronization or a later failure. Changed reuse or a stale decision returns 409. Cross-scope request-ID collisions never reveal audit content.
- Audit actor is explicitly `local-development-unattributed`. It does not pretend to identify an authenticated person. Before real administrative access, replace this development boundary with authenticated authorization and attributable audit identity.

## Consequences

Developers can inspect and deliberately retry retained failures without SQL repair. An unchanged event may fail again if its underlying issue remains. Invalid submissions and denied/conflicting replay requests are not successful administrative transitions; only committed requeues create audit records. There is no edit/delete/bulk replay endpoint.

The database audit is insertion-only through this application, not tamper-proof against database administrators. Production permissions, auditing of access denials, retention, capacity alerts, polished operator/supervisor UI, and compatibility certification remain future work. No user-facing UI is built in this checkpoint.

## Revisit when

Authenticated remote access, high-volume diagnostic paging, or operational supervisor workflows replace this local development tool.
