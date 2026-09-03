# Delivery roadmap

Build risk-first through complete vertical slices. A phase is complete only when its acceptance outcome works end to end.

## Phase 0 — discovery and hardware validation

- Map one pilot plant's real workflow and exception paths.
- Confirm individual, bulk-count, and weight-based tracking needs.
- Prototype the selected barcode scanner, RFID reader, and label printer.
- Define canonical event envelopes and simulate read storms and outages.

**Exit outcome:** selected hardware can be read, normalized, persisted, and replayed without duplicate business effects.

## Phase 1 — walking skeleton

- Establish repository tooling, CI, local containers, migrations, and observability.
- Create empty management UI, operator PWA, cloud API, worker, and edge gateway.
- Send a simulated scan from station to gateway to cloud and display its audit record.

**Exit outcome:** one observable, deployable path crosses all architectural boundaries.

## Phase 2 — first operational journey

- Create a plant, customer, article type, and tracked item or aggregate line.
- Receive, assign to a batch, process, pack, dispatch, and display history.
- Include invalid-transition feedback and basic audit identity.

**Exit outcome:** one realistic item or quantity completes the smallest end-to-end laundry journey.

## Phase 3 — offline and synchronization

- Add PWA caching and emergency queue.
- Add gateway durable storage, outbox/inbox, retry, quarantine, and reconciliation.
- Test multi-day outages, duplicates, lost responses, restarts, and ordering problems.

**Exit outcome:** the Phase 2 journey continues offline and synchronizes correctly after recovery.

## Phase 4 — identity, authorization, and tenancy

- Integrate OpenID Connect.
- Add operator, supervisor, driver, customer, plant-admin, and platform-admin policies.
- Enforce tenant and plant boundaries with integration tests.

**Exit outcome:** representative users can perform only authorized actions on permitted plant data.

## Phase 5 — production depth

- Sorting, containers, batch composition, wash programs, quality, rewash, and discrepancies.
- Fixed RFID and machine adapters behind project interfaces.
- Production and device-health views.

**Exit outcome:** the pilot plant can execute its core production workflow with real devices.

## Phase 6 — logistics and customer operations

- Collections, routes, driver offline workflow, deliveries, proof of delivery, and customer views.

**Exit outcome:** a customer collection-to-return cycle is traceable end to end.

## Phase 7 — contracts and billing

- Effective-dated contracts, price rules, invoice evidence, exports, and adjustments.

**Exit outcome:** an invoice can be reproduced from immutable operational evidence and the effective contract version.

## Phase 8 — production hardening and rollout

- Backup restoration, disaster recovery, security review, load tests, upgrade compatibility, alerting, support tools, and rollback drills.

**Exit outcome:** the system meets the agreed pilot service objectives and has a tested recovery playbook.

