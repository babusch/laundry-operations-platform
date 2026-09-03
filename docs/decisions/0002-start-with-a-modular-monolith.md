# 0002 — Start with a modular monolith

Status: Accepted  
Date: 2026-09-03

## Context

The business model will change as pilot workflows are learned. Premature network boundaries would make cross-domain changes, transactions, testing, and operations more expensive.

## Decision

Implement the cloud backend as an ASP.NET Core modular monolith with explicit domain modules, one API deployment, and one background-worker deployment.

## Consequences

- Domain boundaries can mature without distributed-system overhead.
- Relational transactions remain available for local consistency.
- Modules require architectural tests and review to prevent accidental coupling.
- Individual modules cannot initially scale or deploy independently.

## Revisit when

A measured scaling, availability, security, data-sovereignty, or independent-release need cannot be handled within the modular deployment.

