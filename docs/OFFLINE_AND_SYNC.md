# Offline operation and synchronization

## Reliability objective

Normal receiving, production, packing, and dispatch scans must continue when internet access or the cloud platform is unavailable. Operators must always know whether an action is accepted locally, synchronized, rejected, or requires attention.

## Storage layers

The user-approved initial secure-slice policy allows enrolled sources with locally valid credentials to continue raw scans during internet/cloud identity outages. New enrollment and privilege grants require online authorization initially, as does supervisor replay reauthentication. Central revocation cannot be learned instantly while disconnected. This policy is not yet implemented; see [ADR 0009](decisions/0009-identity-permissions-and-offline-access.md) for boundaries and unresolved credential-lifetime/deployment details.

1. **PWA IndexedDB queue:** temporary protection when a station cannot reach the gateway.
2. **Plant PostgreSQL:** durable store for locally accepted operational events and the subset of reference data needed to operate.
3. **Cloud PostgreSQL:** consolidated multi-plant state and long-term business system of record.

The user interface may show success only after the event is durable in IndexedDB or, preferably, acknowledged by the local gateway. These two states must be visually distinguishable.

## Event envelope

Every device-originated event should carry at least:

```text
event_id                 Globally unique UUID generated at the origin
event_type               Stable, versioned event name
schema_version           Payload schema version
tenant_id
plant_id
station_id
device_id                When applicable
operator_id              When applicable
aggregate_id             Item, container, batch, order, or delivery
occurred_at              Source UTC timestamp
received_at              Set by the receiving gateway or cloud
correlation_id
causation_id             When one operation caused another
payload
```

JSON contracts use camelCase field names. The first concrete contract is `packages/contracts/events/scan-observed.v1.schema.json`; it represents a raw observation, so operator, aggregate, causation, and resolved item fields are not applicable until later processing supplies that context.

## Delivery rules

The gateway accepts `submit-scan.v1` requests on its own Development-only `POST /api/scans`. It commits immutable accepted-event evidence and a pending outbox row in one plant database transaction before acknowledging local acceptance. Gateway time is assigned during transaction preparation and returned only after commit, unchanged on retry. See [ADR 0006](decisions/0006-durable-local-scan-acceptance.md).

The Development-only forwarding worker delivers stored JSON to the local cloud API. Persisted leases, attempt counts, retry times, and cloud receipts support restart recovery and safe replay. Only a validated matching acknowledgement sets `synchronized`; transient failures remain `pending` and permanent responses become `needsAttention`. See [ADR 0007](decisions/0007-forward-plant-outbox-to-local-cloud.md).

Local `/api/sync` diagnostics expose scoped counts, oldest pending age, safe per-event metadata, and replay audit history. A reviewed needs-attention event can be requeued with an idempotent request ID and expected attempt count, committing audit plus queue change atomically without modifying the scan. No remote or authenticated administration is enabled yet. See [ADR 0008](decisions/0008-local-sync-diagnostics-and-audited-replay.md).

Cloud `POST /api/scans` remains restricted to loopback Development use, but now requires a signed, unexpired gateway token for the cloud API audience and `scans.ingest` permission. Tenant/plant registration comes from verified token claims and must match the event before storage. Original JSON and idempotent behavior remain unchanged. See [ADR 0012](decisions/0012-cloud-validates-gateway-tokens.md). Gateway token acquisition and authenticated remote production ingestion remain later work.

- Assume at-least-once delivery, not exactly-once transport.
- Make processing effectively once through idempotency and uniqueness constraints.
- Persist outgoing events in the same transaction as the local state change.
- Record processed event IDs in an inbox before applying remote events.
- Retry transient failures with bounded exponential backoff and jitter.
- Move repeatedly invalid events to an inspectable quarantine/dead-letter state.
- Never silently discard an event or let an endless retry loop hide a permanent failure.

## Conflict handling

Do not use last-write-wins for movements of physical items. The cloud validates each transition against the known state and returns one of:

- Accepted
- Already processed
- Rejected with a business reason
- Held for reconciliation because required history is missing or conflicting

Preserve the original event even when rejected. Supervisors need a reconciliation workflow; do not repair discrepancies by editing history.

Configuration and descriptive reference data may use explicit version numbers and controlled replacement rules. Pricing, contracts, and workflow definitions must retain effective dates so historical processing remains reproducible.

## Reference-data synchronization

Before a shift, the gateway should have the plant's active:

- Customers and delivery points
- Article types and tag mappings
- Workflow and wash-program definitions
- Routes and expected collections/deliveries
- Authorized operators or an offline authentication policy
- Device and station configuration

Use scoped incremental synchronization and tombstones for removals. Avoid copying unrelated tenants or plants to an edge installation.

## Required resilience tests

- Internet disappears before, during, and after scan acknowledgement.
- Cloud accepts an event but the response is lost.
- Gateway restarts with pending outbox events.
- PWA closes with queued IndexedDB events.
- Duplicate events arrive through separate retry paths.
- Events arrive out of order.
- Device clock is wrong.
- RFID reader generates a high-volume duplicate-read storm.
- Cloud and gateway run adjacent supported versions.
- Quarantined events are diagnosed and safely replayed.
