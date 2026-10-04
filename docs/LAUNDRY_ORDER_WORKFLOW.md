# Laundry order workflow

Status: discovery baseline for the pilot laundry. Confirmed behavior is separated from provisional design and open questions. Do not implement an unresolved point as a universal product rule.

Last updated: 2026-10-04. Service-cycle orders are the approved standard direction. Continuous tracking is the approved optional direction for internal cleaning teams. These are product decisions; they are not implemented yet.

## Purpose

A laundry order represents one customer's operational turnaround through the plant. It begins when soiled items are received and continues until the corresponding processed items are verified as outgoing. It is not owned by one operator, station, or scanning session.

The pilot tracks individually tagged textiles. The same physical RFID-tagged items received on an order are expected to be scanned outgoing on that order. A future laundry may use pooled stock, aggregate counts, or weight instead; keep that as a configurable product variation rather than weakening the pilot's exact-item rule.

## Confirmed pilot behavior

### Service-cycle orders

- A standard order groups one customer's items toward a planned delivery date.
- One order may contain several incoming arrivals and several outgoing dispatches. Exact arrival grouping and partial-dispatch rules still require review.
- Orders are created manually by default. Scanning must not silently create an order.
- Automatic order creation may be explicitly enabled for specific customers. Its trigger and selection rules still require review; enabling it must not become the global default.
- If no suitable order exists and automatic creation is disabled, the incoming scan cannot be counted against an order until an operator creates or selects one. The exact prompt and handling of the pending observation remain to be designed.
- Recurring customer schedules are an approved product requirement; their order-generation policy still requires review. Import remains a possible future option.
- Automatic selection must not guess between several suitable open orders. The operator must resolve ambiguous selection.
- Completion requires every received item to be accounted for through outgoing verification or an explicitly authorized exception. Who confirms completion and the detailed exception rules remain open.

### Continuous tracking and charging

- Order handling and charging are independent customer settings: service-cycle orders or continuous tracking, and billable or non-billable service.
- The pilot laundry also washes cleaning textiles/equipment for cleaning teams in the same company. Each team is represented as a laundry customer.
- Those internal teams use continuous tracking with non-billable service. Incoming and outgoing actions are recorded against the team without requiring daily order creation or completion.
- Continuous tracking retains individual movements and item history. It is an ongoing tracking account rather than one ever-growing order.
- Reports show incoming/outgoing quantities by team, article type, and selected time period. Individual staff-recipient tracking is not required for this use case.
- Internal teams must receive the same physical RFID-tagged items they handed in, just like regular customers. Items are not automatically redistributed between teams.
- Each new receipt after a completed return starts a new item turnaround. Match outgoing items to their outstanding incoming receipt; do not reconcile repeated use through lifetime totals alone.
- The same source/operator authorization, customer-selection rules, durable storage, and audit requirements apply to both modes. Non-billable service does not remove traceability requirements.

### Starting receiving and selecting an order

Approved combined start flow:

- Orders can exist in advance as planned work. A planned order is not automatically available for incoming scans merely because its date is near today.
- When the first item is scanned and a planned order needs to be activated, the operator selects the order and confirms **Start receiving on this order**. The first item is then recorded against it.
- Receiving activation and the first item receipt must succeed durably together before success is shown. Retries or concurrent stations must not duplicate the item or activation.
- Receiving activation is shared plant-wide. Other authorized operators and stations can contribute to that order. Customer switching or operator sign-out does not close it.
- Activation records who started receiving and when; it does not substitute for individual item receipt evidence.
- Continuous tracking accounts do not require service-order activation.

Approved initial receiving duration:

- Once receiving starts, the order continues accepting valid incoming items until the whole order is completed, including while processing or dispatch is underway.
- The initial implementation has no separate close-receiving action, receiving-complete marker, or additional confirmation caused solely by processing/dispatch progress.
- New accepted receipts update shared totals and reconciliation. Order completion must account for the latest durable receipts, including concurrent work at other stations.
- Handling new items after whole-order completion remains an open question; this rule does not authorize automatically reopening a completed order.

Proposed selection rules still subject to review: retain a station's explicitly selected suitable order; automatically select when exactly one order is accepting receipts for the identified customer; ask when several qualify; and offer deliberate activation when only planned orders exist. Outgoing selection should use the item's matching incoming record, with explicit resolution when it conflicts with the station's selected order. Dates alone must not resolve ambiguity.

### Missed receiving scan discovered at dispatch

- Ordinary operators may resolve a missing incoming receipt; supervisor approval is not required for this specific action.
- Dispatch flags the affected item when no matching receipt exists. The operator checks the customer and order, confirms the intended attribution, and records **Record missed receipt** with a required reason.
- Save an audited late-receipt correction linked to the actual outgoing observation, item, customer/order or tracking account, operator, station/source, and recording time. The correction and dispatch must be durably and idempotently recorded before success is shown.
- Preserve that the original receiving scan was missing. Do not fabricate a historical scan or invent an actual arrival timestamp when it is unknown.
- Show originally scanned incoming items and receipt corrections distinctly while including both in accounted-for totals.
- Customer mismatch or an existing receipt on another order requires separate conflict resolution; a missed-receipt correction must not silently reassign the item.
- Holding only the affected RFID item while valid items continue, and preventing dispatch completion until unresolved items are addressed, remain proposed behavior for later review.

### Shared order

- An order belongs to one laundry customer and one plant.
- The order is shared throughout the plant. Multiple operators and stations may contribute concurrently.
- Switching a station to another customer does not close the previous customer's order.
- Order totals update from every accepted business action, regardless of which authorized operator or station recorded it.
- An operator's local work session is separate from the shared order. Session totals and order totals must not be conflated.

### Incoming and outgoing operations

- At minimum, scanning supports an incoming/receiving operation and an outgoing/dispatch operation.
- An incoming scan records that the identified physical item was received on the active customer's order.
- An outgoing scan records that the same identified physical item is leaving after processing on that order.
- A raw RFID or barcode observation is not itself an incoming or outgoing action. The business transition also requires authenticated operator and explicit operation context.
- The system must reject or interrupt an outgoing attempt when the item cannot be reconciled with the order's incoming items. Any authorized exception path must be explicit and audited.

### Station operation configuration

Operation purpose and customer-selection behavior are independent configuration dimensions.

Supported operation-purpose patterns:

- **Dedicated incoming:** the station records only receiving actions.
- **Dedicated outgoing:** the station records only dispatch actions.
- **Flexible:** an authorized operator deliberately chooses among the operations permitted at that station.
- A future default-with-switching configuration may offer one default operation while allowing an authorized change.

Supported customer-selection patterns:

- **Automatic/flexible customer:** with no active customer, the first recognized item selects its customer. An item from another customer pauses the flow and asks whether to switch. Declining the switch means that item is not added; accepting changes the station's active customer without closing either customer's order.
- **Locked customer:** the operator deliberately selects a customer. Items owned by another customer are rejected, and changing customer requires a separate deliberate action rather than a quick mismatch prompt.

The interface must never silently add one customer's item to another customer's order.

### Dates and scheduling

- Management staff can create orders in advance when a customer requests service.
- Initial order creation requires customer, planned receiving date, and planned delivery date. Customer reference and notes are optional fields.
- The system supplies the order number, plant attribution, creator, and creation timestamp; the exact initial status remains part of the lifecycle review.
- The planned receiving date can be in the future. Actual receipt timestamps come from accepted incoming actions and remain separate from that date.
- An order has a planned delivery date.
- The planned delivery date can be calculated from customer-specific configuration. For example, a customer may normally receive the order on the next valid delivery day.
- The scheduling model must allow an authorized override with an audit record; exact permissions and required reasons remain to be decided.
- Actual outgoing/dispatch time must be recorded independently from the planned date so the system can explain what was planned and what happened.

### Recurring customer schedules

- A customer can have a reusable service schedule, for example dirty-laundry collection every Monday and clean delivery every Wednesday.
- Each occurrence represents a separate service-cycle order, retaining its own items, dates, incoming/outgoing totals, and exceptions.
- Scheduled collection at the customer's premises and planned receipt at the laundry are distinct dates, even when they fall on the same day. Dispatch from the plant and delivery to the customer are also distinct.
- Configuring a schedule must not implicitly enable automatic order creation. Default manual creation and explicit per-customer automatic creation remain independent of schedule configuration.
- Whether management creates upcoming orders from a schedule in a reviewed batch or enables automatic advance generation remains to be decided.
- Scheduling is part of the order-planning requirement; detailed route planning and driver workflows remain later roadmap work.

Proposed schedule controls for later review: recurrence, collection/delivery weekdays, start/end dates, upcoming-order preview, generation horizon, pause/resume, and one-off changes or skipped occurrences. Holiday handling and propagation of schedule edits to existing orders are unresolved. A schedule change must not silently rewrite historical operational evidence.

### Reconciliation and item exceptions

- The order shows incoming and outgoing totals, including useful article-type breakdowns.
- Reconciliation is item-level for the pilot: the system can identify which specific incoming items have and have not been recorded outgoing.
- A difference is not silently corrected and an item is never deleted from history.
- Lost, damaged, destroyed, and unregistered are distinct concepts and require explicit reason, operator, time, plant, station/source, order, and item attribution as applicable.
- `Unregistered` means an external identity such as an RFID tag was retired or detached from the item. It must not erase the physical item's history.
- Damage does not automatically mean destruction. Later workflow work must decide whether a damaged item is repaired, rewashed, replaced, retired, or destroyed.
- A missing outgoing scan initially represents an unresolved discrepancy, not proof that the item was lost.

## Conceptual boundaries

Keep these concepts separate even if the first UI presents them together:

| Concept | Responsibility |
|---|---|
| Laundry order | Shared customer turnaround from incoming receipt through outgoing verification |
| Continuous tracking account | Ongoing customer/team movement history with item-level turnaround matching and period reports |
| Operator session | One operator's local period of work and personal/session totals |
| Station/source | Trusted physical work origin; does not identify the operator |
| Raw observation | Immutable evidence that a reader or browser observed an identifier |
| Operational action | Operator-attributed interpretation such as item received or item dispatched |
| Production batch | Items grouped for washing or another production step; not synonymous with an order |
| Delivery | Physical/logistical movement to the customer; linked to one or more completed or partially dispatched orders as later decided |
| Item lifecycle event | Damage, destruction, tag retirement, loss resolution, or another change to a physical item's lifecycle |

## Initial order lifecycle direction

The exact state machine is not approved yet. A reasonable starting vocabulary for later review is:

```text
Open -> In processing -> Ready for dispatch -> Dispatching -> Completed
```

The combined receiving-start interaction is approved, but the complete state machine remains provisional. Do not implement the other states until their entry and exit rules are validated. Completion, partial dispatch, reopening, and later corrections still need detailed behavior.

Any later processing or dispatch status must preserve the approved initial rule that valid incoming scans remain possible until whole-order completion.

## Deferred proposal: separate receiving-complete milestone

Status: recorded for future review; not approved for implementation.

A soft **Receiving complete** marker could let an operator indicate that all expected arrivals have been received while the order continues through processing and dispatch. It could help establish an expected incoming quantity and draw attention to late arrivals that may belong to another order.

If reviewed and adopted later, the marker could record the operator and time, allow an explicit audited action to add late items or resume receiving, and preserve the missed-receipt correction path. It would not prove that an unscanned item was absent or close the whole order.

Review whether operators can reliably know that receiving is finished, whether the extra interaction improves accuracy, how expected totals change after additions, and what permissions or confirmation are appropriate. Until a later decision approves this feature, follow the initial open-until-order-completion rule.

## Invariants for future implementation

- One order never mixes laundry customers.
- The pilot's outgoing action references the same immutable textile-item identity recorded incoming.
- An item cannot be counted twice for the same operation within one turnaround through retries or concurrent stations. It can legitimately be received and dispatched again in a later turnaround.
- Shared totals are derived from durable, idempotent business actions rather than mutable counters alone.
- Concurrent operators see convergent order totals; no station owns the order.
- Historical actions and corrections remain auditable and append-only.
- Customer, plant, station/source, operator, operation, order, item, source time, and server receipt time are retained where applicable.
- Offline acceptance follows the platform's durability and synchronization rules without allowing conflicting physical transitions to resolve through generic last-write-wins behavior.

## Provisional assumptions requiring validation

- For customers that explicitly enable automatic creation, the first incoming item may trigger creation of a suitable order. Exact trigger, date, and concurrency rules remain to be decided.
- An operator may explicitly confirm that an order is complete.
- A customer may normally have one suitable open order for a given receiving/delivery cycle.

These points are plausible but were described speculatively and are not approved behavior yet.

## Open questions

1. Can one customer have several arrivals or orders open on the same day?
2. What distinguishes orders: receiving date, collection/load, route stop, customer reference, or another identifier?
3. Who may create, complete, reopen, cancel, or change an order?
4. What happens when another incoming item appears after completion?
5. Can outgoing verification be partial, and can several dispatches fulfill one order?
6. How are weekends, holidays, route schedules, cutoff times, and customer-specific service calendars used to calculate planned delivery?
7. When does an unmatched incoming/outgoing difference become a formal missing-item case?
8. Which damage and retirement reasons are required, and which require supervisor approval?
9. How are replacement items handled when the original item cannot be returned?
10. How are new items, initial stock, and authorized item reassignment introduced into a team's continuous tracking account?
11. How far ahead should scheduled orders be prepared, and should generation be manually confirmed or automatically enabled per customer?
12. When a recurring schedule changes, which existing future orders should be updated and how should staff review those changes?

## First implementation boundary

Before building this workflow, agree on order selection/creation and completion behavior, then define the smallest vertical slice around one customer, one open order, one individually tagged item, one authenticated operator, and incoming plus outgoing actions. Continue using simulated identifiers until real reader hardware is available.
