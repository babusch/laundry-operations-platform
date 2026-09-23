# Laundry order workflow

Status: discovery baseline for the pilot laundry. Confirmed behavior is separated from provisional design and open questions. Do not implement an unresolved point as a universal product rule.

## Purpose

A laundry order represents one customer's operational turnaround through the plant. It begins when soiled items are received and continues until the corresponding processed items are verified as outgoing. It is not owned by one operator, station, or scanning session.

The pilot tracks individually tagged textiles. The same physical RFID-tagged items received on an order are expected to be scanned outgoing on that order. A future laundry may use pooled stock, aggregate counts, or weight instead; keep that as a configurable product variation rather than weakening the pilot's exact-item rule.

## Confirmed pilot behavior

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

- An order has a receiving date.
- An order has a planned delivery date.
- The planned delivery date can be calculated from customer-specific configuration. For example, a customer may normally receive the order on the next valid delivery day.
- The scheduling model must allow an authorized override with an audit record; exact permissions and required reasons remain to be decided.
- Actual outgoing/dispatch time must be recorded independently from the planned date so the system can explain what was planned and what happened.

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

Do not implement those states until their entry and exit rules are validated. In particular, completion, partial dispatch, reopening, late items, and corrections need explicit behavior.

## Invariants for future implementation

- One order never mixes laundry customers.
- The pilot's outgoing action references the same immutable textile-item identity recorded incoming.
- An item cannot be counted twice for the same operation through retries or concurrent stations.
- Shared totals are derived from durable, idempotent business actions rather than mutable counters alone.
- Concurrent operators see convergent order totals; no station owns the order.
- Historical actions and corrections remain auditable and append-only.
- Customer, plant, station/source, operator, operation, order, item, source time, and server receipt time are retained where applicable.
- Offline acceptance follows the platform's durability and synchronization rules without allowing conflicting physical transitions to resolve through generic last-write-wins behavior.

## Provisional assumptions requiring validation

- The first accepted incoming item may select or create the appropriate open order when the customer has no active order.
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
10. Does the exact-same-item rule apply to every pilot customer and article type, or are some flows pooled or quantity-based?

## First implementation boundary

Before building this workflow, agree on order selection/creation and completion behavior, then define the smallest vertical slice around one customer, one open order, one individually tagged item, one authenticated operator, and incoming plus outgoing actions. Continue using simulated identifiers until real reader hardware is available.
