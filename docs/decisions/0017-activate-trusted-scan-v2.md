# 0017 — Activate trusted scan v2

Status: Accepted  
Date: 2026-09-20

## Context

ADR 0015 defined a minimal station request and gateway-enriched v2 event. ADR 0016 retired v1 for new station submissions while requiring immutable queued and historical v1 events to remain deliverable and replayable.

## Decision

The station-facing gateway accepts only `submit-scan.v2`. After authenticating the enrolled source, it adds tenant, plant, station, source, and gateway acceptance time to create `scan.observed.v2`. The station cannot assert those fields or a device ID.

An unchanged request is idempotent only for the same trusted source. Reuse of an event ID by another source is a conflict, even when the origin payload is identical.

Cloud ingestion selects strict validation by `schemaVersion` and accepts `scan.observed.v1` and v2. Persistence retains nullable `device_id` for v1 and nullable `source_id` for v2. Stored v1 payloads are never converted.

## Consequences

New clients have one small request contract and trusted attribution is created at the gateway. Existing outbox records continue synchronizing through upgrades. Database queries must account for version-specific nullable attribution columns.

V2 remains a raw observation, not a completed laundry operation. Operator authentication and workflow authorization are still required and may lead to a later observation version or a separate linked business-action event.

## Revisit when

V1 delivery and retention obligations end, or approved operator/workflow semantics require a new event boundary.
