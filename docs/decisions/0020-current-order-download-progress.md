# 0020 — Scoped current-order downloads with transactional progress

Status: Accepted for the Development cloud slice
Date: 2026-10-17

## Decision

Use a gateway-initiated HTTPS GET returning bounded current published order copies, an opaque protected bookmark and `hasMore`. Require the existing gateway bearer identity plus separate `orders.download` permission; derive tenant/plant from its verified claims. Keep the existing Development loopback boundary and explicit disabled-by-default feature flag.

Allocate a per-plant change position in the same relational transaction as each publication, under the existing plant writer lock. Use a repeatable-read snapshot for each page and a scope/epoch-bound Data Protection cursor. Do not use timestamps, UUID sorting or nontransactional database sequences as the ongoing progress marker. Preserve immutable outbox evidence but do not replay superseded note text to new downloads.

## Consequences

No new service, dependency or transport is introduced. The feed is a changing current-state view, not a frozen snapshot or an event log; retries can return newer versions, and gateway application must be idempotent. Empty results never delete orders. Current-only delivery avoids exporting superseded visible notes but cannot recall data already downloaded/in flight. Customer-change propagation, removals, gateway storage/worker, restore reconciliation and production access remain separate work. Preserve the Data Protection key ring; invalid cursors require explicit recovery, not silent reset. Apply the prepared migration only with writers stopped and separately approved activation. See [contract](../ORDER_DOWNLOAD_CONTRACT.md).
