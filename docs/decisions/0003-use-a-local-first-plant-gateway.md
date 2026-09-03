# 0003 — Use a local-first plant gateway

Status: Accepted  
Date: 2026-09-03

## Context

Plant production cannot stop because a WAN link or cloud service is unavailable. Browsers are also an unsuitable primary integration boundary for fixed industrial equipment and vendor SDKs.

## Decision

Deploy a local .NET gateway with durable plant storage. Operator stations use the gateway over the LAN; fixed readers and machines integrate through gateway adapters. The gateway synchronizes versioned, idempotent events with the cloud.

## Consequences

- Plants can continue operating during internet or cloud outages.
- Hardware integrations and credentials remain inside the plant boundary.
- Edge deployment, monitoring, backup, compatibility, and remote support become product responsibilities.
- Synchronization and reconciliation must be treated as core domain capabilities.

## Revisit when

A plant has no local server capability, or a validated hardware/deployment model provides equivalent durability and isolation with less operational cost.

