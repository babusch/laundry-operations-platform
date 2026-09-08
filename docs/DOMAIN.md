# Domain model

This is a starting vocabulary, not a replacement for workshops with plant operators.

## Core concepts

| Term | Meaning |
|---|---|
| Tenant | Commercial customer of the platform; owns one or more plants |
| Plant | Physical laundry facility with its own gateway and equipment |
| Station | Stable registered work origin within a plant; not inherently a fixed operational role |
| Device | Physical scanner/reader or enrolled adapter associated with a station |
| Operation | Intended task, such as receiving, dispatch, lookup, or inventory counting; separate from source identity |
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

### Station identity and operational purpose

Approved by the user on 2026-09-08: keep station identity stable while making operational purpose configurable. Support dedicated stations, flexible stations with a choice of permitted operations, and stations with a default operation that can be switched. Available operations depend on plant configuration, applicable user permissions, and hardware capabilities.

A fallback workstation can perform an allowed operation while retaining its actual station/device identity; it must not impersonate an unavailable workstation. Source authorization restricts which identity may be claimed, not necessarily the number of operations available there.

A raw scan observation alone does not mean receipt or dispatch. Business interpretation requires explicit operation context; lookup must not create a receiving transition. When workflow processing is introduced, preserve the operation context applicable at capture so later station configuration changes or delayed delivery do not reinterpret historical scans. The existing raw-observation v1 contract remains unchanged; the representation of workflow context is deferred to that slice.

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
