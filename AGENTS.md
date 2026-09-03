# Agent instructions

## Mission

Build a reliable industrial laundry operations platform. Correctness, traceability, offline continuity, and operator usability take priority over novelty or premature scale.

## Read before changing code

1. Read `docs/PROJECT_STATUS.md` and the nearest `AGENTS.md` in the area being changed.
2. Read the relevant canonical document:
   - Product behavior or scope: `docs/PRODUCT.md`
   - Components, boundaries, or technology: `docs/ARCHITECTURE.md`
   - Offline behavior, plant gateway, or synchronization: `docs/OFFLINE_AND_SYNC.md`
   - Entities, terminology, or business events: `docs/DOMAIN.md`
   - Milestone selection or sequencing: `docs/ROADMAP.md`
   - Repository placement: `docs/REPOSITORY_STRUCTURE.md`
3. Check `docs/decisions/` before revisiting an architectural choice.

## Non-negotiable system invariants

- A loss of internet or cloud availability must not stop normal shop-floor scanning.
- An accepted scan must be durably stored before success is shown to the operator.
- Every device-originated event has a globally unique ID and is safe to retry.
- Cloud ingestion is idempotent. Duplicate delivery must not duplicate a physical movement or charge.
- Do not resolve physical inventory conflicts with generic last-write-wins behavior.
- Preserve an append-only audit trail for operational transitions and sensitive administrative actions.
- Enforce tenant and plant boundaries in application logic and automated tests.
- Keep hardware-vendor SDKs behind adapters. Domain modules must not depend on vendor libraries.
- Never put secrets, production credentials, customer data, or real tag identifiers in the repository.

## Architecture rules

- Keep the cloud backend a modular monolith until an architecture decision record documents a concrete need to extract a service.
- Keep domain rules inside the owning module; do not create a shared miscellaneous business-logic package.
- Share stable contracts, generated API clients, and UI components—not persistence entities.
- Use OpenAPI to generate TypeScript API clients.
- Use transactional outbox/inbox patterns across durable asynchronous boundaries.
- Treat timestamps as UTC in storage; retain source-device time and server-receipt time where operationally relevant.
- Database schema changes require migrations. Never depend on automatic production schema mutation.
- A new production dependency, service, protocol, or datastore requires a short ADR when it changes an architectural boundary.

## Implementation workflow

- Work in small vertical slices that leave the repository runnable.
- Start with the acceptance criteria in `docs/ROADMAP.md`; do not implement later phases speculatively.
- Add or update tests with behavior changes.
- Include outage, retry, duplicate, and out-of-order cases when changing synchronization or device ingestion.
- Update canonical documentation and `docs/PROJECT_STATUS.md` when a decision, milestone, or assumption changes.
- Prefer a simulator or recorded fixture for hardware integration tests; keep real-device tests as an explicit separate suite.
- Report commands run and any checks not run in the handoff.

## Definition of done

- The relevant behavior and failure modes are tested.
- Logs and diagnostics contain correlation identifiers but no sensitive data.
- Operator-facing failure states explain whether work is local, queued, synchronized, or needs attention.
- Documentation matches the implemented behavior.
- No unrelated changes are included.

## Nested instructions

Add a focused `AGENTS.md` inside an application only when that area has commands or constraints not shared by the rest of the repository. Keep root guidance authoritative and avoid copying it into nested files.

