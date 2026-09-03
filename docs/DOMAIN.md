# Domain model

This is a starting vocabulary, not a replacement for workshops with plant operators.

## Core concepts

| Term | Meaning |
|---|---|
| Tenant | Commercial customer of the platform; owns one or more plants |
| Plant | Physical laundry facility with its own gateway and equipment |
| Laundry customer | Hotel, hospital, care facility, restaurant, or other serviced organization |
| Article type | Product definition such as sheet, towel, garment, or mat |
| Textile item | Individually identifiable physical article, commonly carrying RFID |
| Quantity line | Aggregate count or weight when individual tracking is not used |
| Container | Bag, trolley, cage, cart, or other handling unit |
| Production batch | Group processed together through one or more production steps |
| Process run | Actual execution of a wash, dry, finish, or other machine/program step |
| Order | Requested or expected laundry service |
| Delivery | Collection or distribution movement to a customer location |
| Scan event | Observation or action originating at a station or device |
| Exception | State requiring operator or supervisor intervention |

## Identity principles

- Internal IDs are immutable UUIDs.
- Human-readable numbers are separate attributes and may follow tenant-specific sequences.
- RFID EPC, barcode, and vendor identifiers are external identities that can be assigned, retired, or replaced.
- Never use a mutable tag value as the database primary key.
- Model individually tracked items and aggregate quantity/weight flows explicitly; do not pretend one is the other.

## Representative events

```text
CollectionRecorded
ContainerReceived
ItemIdentified
QuantityReceived
ItemAssignedToContainer
ContainerAssignedToBatch
BatchStarted
ProcessRunCompleted
QualityCheckPassed
ItemRejected
RewashRequested
ContainerPacked
DispatchVerified
DeliveryConfirmed
InventoryDiscrepancyRaised
```

Event names describe facts that happened. Commands such as `StartBatch` or `ConfirmDelivery` request a transition; they are not historical events.

## Initial state-machine guidance

State transitions must be explicit and validated. A scan should represent an operational intent in context—not merely change an arbitrary status field. Invalid transitions return actionable feedback while retaining the attempted event for diagnostics.

Detailed state machines belong beside their owning module once workflows are validated with real users. Record material changes to shared terminology here.

