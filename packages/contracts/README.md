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
