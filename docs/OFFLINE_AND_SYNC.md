# Offline operation and synchronization

## Reliability objective

Normal receiving, production, packing, and dispatch scans must continue when internet access or the cloud platform is unavailable. Operators must always know whether an action is accepted locally, synchronized, rejected, or requires attention.

## Storage layers

The user-approved initial secure-slice policy allows enrolled sources with locally valid credentials to continue raw scans during internet/cloud identity outages. Gateway-to-cloud identity is now implemented for loopback Development: a Keycloak outage or disabled gateway account leaves durable outbox events pending while local acceptance continues. New station enrollment and privilege grants still require online authorization initially, as does supervisor replay reauthentication. Disabling an account prevents new tokens; an already-issued signed token remains valid until its five-minute expiry, so central revocation is bounded rather than instant. The broader policy is not yet complete; see [ADRs 0009](decisions/0009-identity-permissions-and-offline-access.md) and [0013](decisions/0013-gateway-acquires-short-lived-tokens.md).

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

The gateway accepts `submit-scan.v1` requests on its own Development-only `POST /api/scans` only after loopback HTTPS source-cookie authentication, explicit `scans.submit` authorization, antiforgery validation, and trusted tenant/plant/station scope enforcement. It commits immutable accepted-event evidence and a pending outbox row in one plant database transaction before acknowledging local acceptance. Gateway time is assigned during transaction preparation and returned only after commit, unchanged on retry. The v1 `deviceId` is untrusted compatibility metadata, not authorization; device identity will not be mandatory in the replacement contract. Source authentication does not replace the authenticated operator/workflow context required before production scans become business actions. See [ADRs 0006](decisions/0006-durable-local-scan-acceptance.md) and [0014](decisions/0014-authenticate-local-scan-submission.md).

The Development-only forwarding worker delivers stored JSON to the local cloud API. Persisted leases, attempt counts, retry times, and cloud receipts support restart recovery and safe replay. Only a validated matching acknowledgement sets `synchronized`; transient failures remain `pending` and permanent responses become `needsAttention`. See [ADR 0007](decisions/0007-forward-plant-outbox-to-local-cloud.md).

Local `/api/sync` diagnostics expose scoped counts, oldest pending age, safe per-event metadata, and replay audit history. A reviewed needs-attention event can be requeued with an idempotent request ID and expected attempt count, committing audit plus queue change atomically without modifying the scan. No remote or authenticated administration is enabled yet. See [ADR 0008](decisions/0008-local-sync-diagnostics-and-audited-replay.md).

Cloud `POST /api/scans` remains restricted to loopback Development use, but now requires a signed, unexpired gateway token for the cloud API audience and `scans.ingest` permission. Tenant/plant registration comes from verified token claims and must match the event before storage. The gateway acquires and caches short-lived tokens without making identity availability part of local acceptance. Original JSON and idempotent behavior remain unchanged. See [ADRs 0012](decisions/0012-cloud-validates-gateway-tokens.md) and [0013](decisions/0013-gateway-acquires-short-lived-tokens.md). Authenticated remote production ingestion remains later work.

Cloud signing-key metadata refresh is enabled for planned identity-provider rollover. Normal rotation retains the previous public key during an overlap while new tokens use the new active key. An initial new-key request can receive a transient 401 as refresh begins; the sender retries the immutable event and retains it durably if a later attempt is needed. Unknown keys not published by the configured authority remain rejected.

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
- Identity provider is unavailable before token acquisition; local acceptance continues and queued events synchronize unchanged after recovery.
- Gateway identity is disabled before token acquisition; no cloud delivery occurs, local evidence remains pending, and recovery preserves the event ID and payload.
- Identity signing key rotates while cloud metadata is cached; the new key is learned, the overlapping previous key remains valid, and an unrelated key remains rejected.
- Cloud accepts an event but the response is lost.
- Gateway restarts with pending outbox events.
- PWA closes with queued IndexedDB events.
- Duplicate events arrive through separate retry paths.
- Events arrive out of order.
- Device clock is wrong.
- RFID reader generates a high-volume duplicate-read storm.
- Cloud and gateway run adjacent supported versions.
- Quarantined events are diagnosed and safely replayed.
