# 0016 — Retire v1 for new station submissions

Status: Accepted  
Date: 2026-09-20

## Context

`submit-scan.v1` was a provisional walking-skeleton contract. It has no production consumers, but the Development gateway currently accepts it and plant storage contains immutable v1 observations that may still be forwarded or replayed. Implementing both v1 and v2 for new station submissions would add compatibility complexity without protecting a real installed client.

The v2 contract is also not declared a final production contract. Authenticated operator and workflow context remains unresolved and may require a later contract or, preferably where semantics differ, a separate operational command/event linked to the raw observation.

## Decision

Freeze v1 immediately as legacy: no new fields, producers, or station clients. During the runtime migration, replace the station-facing gateway request with v2 rather than adding parallel v1/v2 station submission.

Retain v1 schemas, examples, and validation so historical evidence remains interpretable. The gateway outbox and cloud ingestion must continue delivering and accepting already stored v1 events unchanged until no v1 event can require delivery or replay. Never rewrite a stored v1 observation into v2.

Do not assume operator authentication means the browser should submit an `operatorId`. Operator identity must be established independently by authentication. When operator/workflow design is approved, either the gateway will add trusted operator/workflow attribution to a newly versioned event or the system will create a separate business-action event linked to `scan.observed.v2`. Choose based on event meaning, not a predetermined version number.

## Consequences

The future PWA targets v2 only, and the gateway has one current station request contract after migration. Cloud support for v1 remains a historical-consumer responsibility during the transition. V2 may later become legacy, but no contract is called final prematurely.

## Revisit when

All v1 outbox/replay obligations are gone, operator/workflow authentication is designed, or retention policy permits removal of historical schema support.
