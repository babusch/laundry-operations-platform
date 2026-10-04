# Product and scope

## Vision

Create a professional industrial laundry platform that gives operators a fast and dependable shop-floor experience while providing customers and managers with accurate inventory, production, delivery, quality, and billing information.

## Reusable product and pilot approach

The first laundry is a pilot for a reusable product, not a customer-specific codebase. Support different companies (tenants), multiple plants, and differing scanner/hardware installations through the same maintained core.

- Configure company/plant identity, stations, trusted sources, optional equipment assignments, and supported operational variations rather than hard-coding pilot values or creating per-customer forks.
- Keep vendor protocols and SDKs behind hardware adapters that produce the shared scan contract. Replacing supported hardware should require configuration/enrollment, not domain changes.
- Make installation, configuration, upgrades, and recovery repeatable and documented. Validate supported device models, operating systems, and connection methods; arbitrary hardware is not automatically compatible.
- Deliver this incrementally using the pilot's real workflow and a simulator. Do not build a speculative universal workflow engine or every hardware adapter upfront.

## Primary users

Station behavior is configurable: dedicated operation, flexible operation selection, or a default with permitted switching. A laundry need not assign fixed operational roles to its stations. Authorized work can move to another capable station without falsifying its origin. See [station terminology and behavior](DOMAIN.md#station-identity-and-operational-purpose). This is an approved product requirement, not an implemented station-management feature.

- Plant operator: receives, sorts, processes, packs, and dispatches laundry.
- Supervisor: manages exceptions, production flow, quality, and staffing decisions.
- Driver: performs collections, deliveries, and proof of delivery.
- Customer user: views orders, stock, deliveries, discrepancies, and documents.
- Plant administrator: configures stations, trusted sources, workflows, local users, and equipment where the installation needs explicit equipment management.
- Platform administrator: manages tenants, plants, integrations, and support access.

## Core operational journey

The pilot's order-level incoming/outgoing behavior is documented in [Laundry order workflow](LAUNDRY_ORDER_WORKFLOW.md). That document distinguishes confirmed plant behavior from assumptions that still require validation.

Service-cycle orders are the standard customer workflow. Optional continuous tracking supports internal cleaning teams that need incoming/outgoing history without daily order completion. Charging is configured independently; the pilot's internal teams are non-billable. Both pilot workflows require the same RFID-tagged items to return to the customer/team that supplied them.

Order creation is manual by default. Automatic creation is an explicit per-customer option; recognizing a customer from a scan does not itself authorize creating an order.

To begin receiving on a planned order, an operator selects it and confirms starting receiving as part of recording the first incoming item. That activation is shared across the plant. Ordinary operators may also resolve a missed receiving scan discovered at dispatch by confirming the customer/order and recording a reasoned, audited late-receipt correction before dispatch succeeds.

Management can create orders in advance with customer, planned receiving date, and planned delivery date, plus optional customer reference and notes. Actual incoming/outgoing timestamps remain separate from planned dates. Recurring customer schedules should make arrangements such as Monday collection and Wednesday delivery easy to configure; each occurrence has its own service-cycle order. A schedule does not implicitly enable automatic generation. Collection/receipt and dispatch/customer delivery remain distinct milestones; generation policy and calendar exceptions still require review.

1. Collection or customer handoff
2. Receiving and identification
3. Sorting and classification
4. Batch or production-run assignment
5. Washing and finishing
6. Quality control, rejection, or rewash
7. Packing and dispatch verification
8. Delivery and proof of delivery
9. Reconciliation and billing

Every material transition must be traceable to time, plant, trusted source/station, operator when applicable, and the affected physical or aggregate unit. Attribute specific equipment only when that identity is actually available; do not describe keyboard-like input as authenticated hardware.

Before a scan can count as a receiving, sorting, packing, dispatch, inventory, or other operational action, both the source/station and the active operator must be authenticated and authorized for that workflow. A device ID is not required: stations may use several scanners, and equipment attribution stays optional unless the integration can establish it reliably.

### Production station workspace direction

The pilot laundry is predominantly RFID-based, so the production station experience should optimize for rapid multi-item RFID capture while continuing to support barcode and explicit manual work where needed. Barcode-first simulator slices are infrastructure proofs, not a decision to make the finished product barcode-centric.

The production scan workspace is expected to show at least the active order and laundry customer, totals for the current work session and active customer/order, and a useful breakdown by article type. Exact measures and grouping remain subject to workflow observation with the pilot plant.

Operators also need explicit ways to record items or quantities that cannot be scanned and to document deviations or other operational problems. These are operator-attributed, auditable business actions with reasons and workflow context. They must not be represented as fabricated barcode or RFID observations.

## Initial product boundary

After receiving starts, valid incoming scans remain allowed until the whole order is completed, including during processing and dispatch. A separate receiving-complete milestone is documented as a deferred proposal in the workflow document and requires future review before implementation.

Ordinary operators explicitly confirm whole-order completion after reviewing incoming, outgoing, and exception totals. Every received item must be dispatched or have a recorded disposition; unresolved items keep the order open. Completion records the operator and time and stops further incoming/outgoing actions on the order. Matching totals never trigger automatic completion.

Build first:

- Tenants, plants, users, and roles
- Customers and article types
- Physical items and aggregate containers/batches
- Barcode/RFID identity and scan ingestion
- Receiving, production, packing, and dispatch states
- Offline plant operation and cloud synchronization
- Audit history and operational exception handling
- Basic inventory and production views

Build after operational data is trustworthy:

- Route planning and driver workflows
- Customer portal and notifications
- Contract pricing and invoice generation
- Advanced forecasting, optimization, and analytics

## Out of scope until explicitly approved

- A microservice per domain module
- Kubernetes as an initial requirement
- Full event sourcing of every business entity
- Direct browser control of fixed industrial equipment
- Vendor-specific concepts leaking into the core domain
- AI-based production optimization before reliable operational data exists

## Product qualities

The system should be:

- Offline-capable at the plant
- Fast with gloves, touchscreens, and scanners
- Explicit about success, rejection, queued work, and synchronization
- Auditable and explainable
- Multi-plant and tenant-isolated
- Recoverable after device, network, gateway, or cloud failures
- Accessible and usable in noisy industrial environments
- Multilingual, with English and Swedish prioritized for the pilot while allowing additional languages without changing workflows or event contracts
