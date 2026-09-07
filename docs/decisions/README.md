# Architecture decision records

ADRs preserve why a consequential decision was made. They are short, immutable after acceptance except for status and links. Supersede an old ADR with a new one instead of rewriting history.

## Index

- [0001 — Use a monorepo](0001-use-a-monorepo.md)
- [0002 — Start with a modular monolith](0002-start-with-a-modular-monolith.md)
- [0003 — Use a local-first plant gateway](0003-use-a-local-first-plant-gateway.md)
- [0004 — Bootstrap local scan ingestion](0004-bootstrap-local-scan-ingestion.md)
- [0005 — Use PostgreSQL for plant storage](0005-use-postgresql-for-plant-storage.md)
- [0006 — Durable local scan acceptance](0006-durable-local-scan-acceptance.md)
- [0007 — Forward the plant outbox to the local cloud](0007-forward-plant-outbox-to-local-cloud.md)

## Template

```markdown
# NNNN — Decision title

Status: Proposed | Accepted | Superseded
Date: YYYY-MM-DD

## Context

What forces and constraints require a decision?

## Decision

What are we choosing?

## Consequences

What becomes easier, harder, or intentionally deferred?

## Revisit when

What measurable trigger should cause a review?
```
