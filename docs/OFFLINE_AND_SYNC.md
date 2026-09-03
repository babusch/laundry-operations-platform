# Offline operation and synchronization

## Reliability objective

Normal receiving, production, packing, and dispatch scans must continue when internet access or the cloud platform is unavailable. Operators must always know whether an action is accepted locally, synchronized, rejected, or requires attention.

## Storage layers

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

## Delivery rules

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

