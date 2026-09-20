# Contracts

This package contains language-neutral, versioned schemas shared across deployable components. A contract describes data exchanged across a durable boundary; it does not contain processing or persistence logic.

## Scan observed v1

`events/scan-observed.v1.schema.json` describes a raw barcode or RFID observation accepted by a plant gateway.

The event deliberately contains observation facts rather than item master data:

- `eventId` is generated at the origin and must remain unchanged across retries.
- `eventType` is always `scan.observed` for this schema.
- `correlationId` connects this observation to related logs and later events.
- Tenant, plant, and station IDs describe the source boundary. The provisional v1 `deviceId` is origin-supplied capture metadata with limited assurance; it is not physical-device authentication.
- `observedAtUtc` is the source clock reading.
- `gatewayAcceptedAtUtc` is recorded only after durable local acceptance.
- `identifier` records either a barcode or RFID value and may be unknown to the system.

The synthetic examples contain no real customer or tag data.

## Scan v1 — historical compatibility

Status: **legacy and frozen**. No new station client or PWA may adopt v1. Its schemas and examples remain so historical stored events can still be validated, delivered, and replayed unchanged.

`requests/submit-scan.v1.schema.json` records the retired station request for interpretation and tests. This provisional contract predates the approved trusted-source boundary; its required `deviceId` never proved which keyboard-wedge scanner emitted input.

The gateway no longer accepts new v1 requests. Existing `scan-observed.v1` events remain immutable, and cloud ingestion continues accepting them while delivery, replay, or retention obligations exist.

## Scan v2 — current station and gateway contract

`requests/submit-scan.v2.schema.json` is the strict minimal station-to-gateway request. Its barcode and RFID examples contain only origin-owned observation data: version, event/correlation IDs, event type, observation time, and identifier. Tenant, plant, station, source, device, gateway-acceptance, and operator fields are rejected.

`events/scan-observed.v2.schema.json` is the corresponding accepted event. The gateway preserves the request fields and adds trusted `tenantId`, `plantId`, `stationId`, and `sourceId` from the authenticated `SourceIdentity`, plus `gatewayAcceptedAtUtc` after durable local acceptance. `sourceId` identifies the enrolled browser or adapter, not a physical scanner or human.

No equipment or reader field is included. Optional hardware attribution can be versioned in after a real integration establishes what can be verified. Operator identity also remains separate: an authenticated operator/workflow is required before a raw observation can count as an operational action.

Submit v2 requests to the Development gateway using trusted `https://localhost:7200`, an enrolled source cookie, and its antiforgery token. The gateway validates v2 only, durably stores the request and enriched event with one pending outbox row, then acknowledges it. A retry uses the same event ID and every original request field unchanged, from the same enrolled source. The cloud accepts both current v2 and historical v1 events.

## Evolution rules

- Correct descriptions without changing the schema's meaning or accepted data.
- Create a new schema version when adding or removing a field, making an optional field required, changing a field's meaning or representation, or otherwise changing accepted data. The strict v1 schema rejects unknown fields so accidental payload growth is visible.
- Keep old schemas and their tests while any supported cloud, gateway, or station version can still produce them.
- Never rewrite historical events merely to make them resemble a newer contract.
- Freezing a producer version does not remove its schema: consumers may need it to validate retained, queued, or replayed historical events.

RFID antenna data, signal strength, read aggregation, barcode symbology, operator context, equipment attribution, device sequencing, and item resolution remain intentionally deferred until validated workflows or hardware require them.

## Validation

From the repository root:

```powershell
corepack pnpm test:contracts
```
