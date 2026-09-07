# Contracts

This package contains language-neutral, versioned schemas shared across deployable components. A contract describes data exchanged across a durable boundary; it does not contain processing or persistence logic.

## Scan observed v1

`events/scan-observed.v1.schema.json` describes a raw barcode or RFID observation accepted by a plant gateway.

The event deliberately contains observation facts rather than item master data:

- `eventId` is generated at the origin and must remain unchanged across retries.
- `eventType` is always `scan.observed` for this schema.
- `correlationId` connects this observation to related logs and later events.
- Tenant, plant, station, and device IDs establish the source boundary.
- `observedAtUtc` is the source clock reading.
- `gatewayAcceptedAtUtc` is recorded only after durable local acceptance.
- `identifier` records either a barcode or RFID value and may be unknown to the system.

The synthetic examples contain no real customer or tag data.

## Submit scan v1

`requests/submit-scan.v1.schema.json` is the station-to-gateway request, with synthetic `submit-scan.v1.barcode.json` and `submit-scan.v1.rfid.json` examples. It contains the observation fields but rejects `gatewayAcceptedAtUtc`: the gateway owns that timestamp. Both submission examples use the default development simulator station/device tuple.

The gateway prepares the acceptance timestamp and event in its transaction, and exposes them only after commit. The stored accepted event satisfies `scan-observed.v1` and is forwarded unchanged. Source timestamps and identifier strings are not rewritten. Tests keep the request's common field definitions aligned with the event contract.

Submit requests to the gateway on port 5200; the cloud on port 5100 expects accepted events instead. A retry uses the same event ID and every original field unchanged. A new physical observation gets a new ID. The HTTP receipt distinguishes `acceptedLocally` (201) and `alreadyAcceptedLocally` (200). `deliveryStatus` reports `pending`, `synchronized`, or `needsAttention` as recorded locally at the time of the receipt. Local acceptance alone does not claim synchronization. Tenant/plant/station/device fields must match trusted scope, not establish their own authority.

## Evolution rules

- Correct descriptions without changing the schema's meaning or accepted data.
- Create a new schema version when adding or removing a field, making an optional field required, changing a field's meaning or representation, or otherwise changing accepted data. The strict v1 schema rejects unknown fields so accidental payload growth is visible.
- Keep old schemas and their tests while any supported cloud, gateway, or station version can still produce them.
- Never rewrite historical events merely to make them resemble a newer contract.

RFID antenna data, signal strength, read aggregation, barcode symbology, operator context, device sequencing, and item resolution remain intentionally deferred until validated workflows or hardware require them.

## Validation

From the repository root:

```powershell
corepack pnpm test:contracts
```
