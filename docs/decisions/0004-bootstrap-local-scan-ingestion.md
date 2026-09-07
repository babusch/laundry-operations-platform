# 0004 — Bootstrap local scan ingestion with strict boundary validation

Status: Accepted
Date: 2026-09-07

## Context

The walking skeleton needs to persist simulated scans before gateway identity and deployment choices are settled. Incoming tenant and plant IDs cannot establish their own authority. Retrying a committed event must be safe even after losing its acknowledgement.

## Decision

- Expose `POST /api/scans` only in Development when `ScanIngestion:Enabled` is true, and require a loopback remote address.
- Resolve the permitted tenant and plant from server configuration. Reject body values outside that scope. No caller-selected headers establish scope.
- This is a local simulator boundary, not gateway authentication. Do not enable this route in Staging or Production. Replace the local access check with authenticated gateway identity and scoped claims before accepting network clients.
- Validate with JsonSchema.Net against the authoritative JSON Schema embedded from `packages/contracts`, including UUID and timestamp formats. No runtime Node process or independently maintained C# validation schema is needed.
- Use an atomic PostgreSQL insert with `ON CONFLICT (event_id) DO NOTHING`, then read only within the permitted tenant and plant.
- Preserve original JSON in a nullable additive column. Compare JSON values for retries, ignoring object property order and whitespace. String values, including timestamp spellings, remain exact. This avoids precision loss in relational timestamps incorrectly classifying a retry.
- Return a cloud receipt only after storage confirms the observation. Storage outages have an uncertain commit outcome; clients retain and retry the same event ID and payload.

## Consequences

This checkpoint needs no external identity provider and is limited to development on one computer. Loopback access is not user authentication; anyone with local access can submit synthetic observations for the configured scope.

The table acts as the first ingestion inbox. There are no downstream operational mutations or outgoing messages yet. Future projection/outbox writes must share an appropriate transaction with ingestion.

The migration preserves existing rows. Legacy rows lacking original JSON cannot be proved identical to a replay and return 409 without being overwritten. A conflict never exposes a different tenant's stored values.

Body size is limited to 16 KiB and JSON depth to eight. Ambiguous duplicate properties are rejected. The current storage adapter also rejects timestamps not representable by .NET and identifiers containing NUL, which PostgreSQL text cannot store. These adapter limits do not redefine the language-neutral schema.

## Revisit when

A simulator or gateway runs on another machine, a staging deployment is introduced, downstream business processing begins, or contract evolution requires a different payload comparison policy.
