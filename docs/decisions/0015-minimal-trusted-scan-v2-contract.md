# 0015 — Minimal request and trusted event for scan v2

Status: Accepted  
Date: 2026-09-20

## Context

The provisional `submit-scan.v1` request repeats tenant, plant, and station IDs already established by authenticated source enrollment, and requires an origin-supplied `deviceId` that cannot reliably identify keyboard-style scanners. Preserving those fields in a new operator PWA would make untrusted request data look authoritative and would retain a physical-device requirement the product has rejected.

The cloud still needs durable trusted attribution for each raw observation. Operator identity is a separate concern: an enrolled browser or adapter does not identify the human, and a raw observation must not become a laundry business action without later authenticated operator/workflow authorization.

## Decision

Define `submit-scan.v2` as the minimal strict station-to-gateway request containing only schema version, event ID, event type, correlation ID, origin observation time, and the observed barcode/RFID identifier. It contains no tenant, plant, station, source, physical-device, or operator identity.

Define `scan-observed.v2` as the immutable accepted event. The gateway preserves every request field and adds tenant ID, plant ID, station ID, and source ID from the authenticated local `SourceIdentity`, plus its durable acceptance timestamp. `sourceId` identifies the enrolled browser or adapter—not a physical scanner or operator.

Do not add speculative equipment, antenna, signal-strength, read-session, barcode-symbology, or operator fields. Add them through a later version only when a validated workflow or hardware integration establishes their meaning and assurance.

Keep the strict v1 schemas and examples. This decision adds contracts only: the running gateway and cloud continue using v1 until a separate implementation slice adds parallel v2 validation, enrichment, persistence, forwarding, and ingestion tests. Historical v1 observations are never rewritten.

## Consequences

The future PWA sends a smaller request and cannot claim its own tenant, plant, station, or trusted source. Gateway enrichment makes the trust boundary visible in the event. Stations are free to use multiple scanners without inventing a required device record.

Supporting v2 at runtime will require coordinated gateway and cloud changes while v1 remains supported during transition. Operator authentication and workflow authorization remain mandatory before observations can cause or confirm operational transitions.

## Revisit when

Implementing runtime v2 support, validating Datamars/Cloudburst or another hardware interface, or designing authenticated operator workflow commands and events.
