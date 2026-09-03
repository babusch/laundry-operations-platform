# 0001 — Use a monorepo

Status: Accepted  
Date: 2026-09-03

## Context

The operator PWA, management UI, cloud backend, edge gateway, schemas, simulators, and deployment definitions will evolve together during early product development. End-to-end changes often cross several of these boundaries.

## Decision

Keep all project components in one Git repository while preserving independently deployable applications and explicit contracts.

## Consequences

- Cross-component changes and compatibility tests can be atomic.
- Shared UI and generated contract workflows are simpler.
- CI must use path-aware jobs as the repository grows.
- Repository visibility cannot differ by component without a future split.

## Revisit when

A component has separate ownership, access restrictions, release governance, or distribution requirements that materially conflict with the monorepo.

