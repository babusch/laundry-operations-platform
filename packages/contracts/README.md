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

## Submit scan v1

`requests/submit-scan.v1.schema.json` is the station-to-gateway request, with synthetic `submit-scan.v1.barcode.json` and `submit-scan.v1.rfid.json` examples. It contains the observation fields but rejects `gatewayAcceptedAtUtc`: the gateway owns that timestamp. Both submission examples use the default development simulator values. This provisional contract predates the approved trusted-source boundary; do not infer that its required `deviceId` proves which keyboard-wedge scanner emitted input. The authenticated endpoint deliberately ignores `deviceId` for authorization. A future version will remove mandatory device identity and keep equipment/capture attribution optional.

The gateway prepares the acceptance timestamp and event in its transaction, and exposes them only after commit. The stored accepted event satisfies `scan-observed.v1` and is forwarded unchanged. Source timestamps and identifier strings are not rewritten. Tests keep the request's common field definitions aligned with the event contract.

Submit requests to the Development gateway using trusted `https://localhost:7200`, an enrolled source cookie, and its antiforgery token; the cloud on port 5100 expects accepted events instead. A retry uses the same event ID and every original field unchanged. A new physical observation gets a new ID. The HTTP receipt distinguishes `acceptedLocally` (201) and `alreadyAcceptedLocally` (200). `deliveryStatus` reports `pending`, `synchronized`, or `needsAttention` as recorded locally at the time of the receipt. Local acceptance alone does not claim synchronization or an authorized business action. Tenant/plant/station request fields must match trusted scope, not establish their own authority. The v1 `deviceId` remains unchanged evidence and is not used to authenticate a physical scanner. Authenticated operator/workflow context is required before production scans can count as laundry operations.

## Scan v2 — contract defined, runtime not implemented

`requests/submit-scan.v2.schema.json` is the strict minimal station-to-gateway request. Its barcode and RFID examples contain only origin-owned observation data: version, event/correlation IDs, event type, observation time, and identifier. Tenant, plant, station, source, device, gateway-acceptance, and operator fields are rejected.

`events/scan-observed.v2.schema.json` is the corresponding accepted event. The gateway preserves the request fields and adds trusted `tenantId`, `plantId`, `stationId`, and `sourceId` from the authenticated `SourceIdentity`, plus `gatewayAcceptedAtUtc` after durable local acceptance. `sourceId` identifies the enrolled browser or adapter, not a physical scanner or human.

No equipment or reader field is included. Optional hardware attribution can be versioned in after a real integration establishes what can be verified. Operator identity also remains separate: an authenticated operator/workflow is required before a raw observation can count as an operational action.

These v2 files are design contracts only. The running gateway and cloud still accept, store, and forward v1. Do not send v2 to either API until the runtime implementation slice is complete.

## Evolution rules

- Correct descriptions without changing the schema's meaning or accepted data.
- Create a new schema version when adding or removing a field, making an optional field required, changing a field's meaning or representation, or otherwise changing accepted data. The strict v1 schema rejects unknown fields so accidental payload growth is visible.
- Keep old schemas and their tests while any supported cloud, gateway, or station version can still produce them.
- Never rewrite historical events merely to make them resemble a newer contract.

RFID antenna data, signal strength, read aggregation, barcode symbology, operator context, equipment attribution, device sequencing, and item resolution remain intentionally deferred until validated workflows or hardware require them.

## Validation

From the repository root:

```powershell
corepack pnpm test:contracts
```
