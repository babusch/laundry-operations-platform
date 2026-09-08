# Product and scope

## Vision

Create a professional industrial laundry platform that gives operators a fast and dependable shop-floor experience while providing customers and managers with accurate inventory, production, delivery, quality, and billing information.

## Reusable product and pilot approach

The first laundry is a pilot for a reusable product, not a customer-specific codebase. Support different companies (tenants), multiple plants, and differing scanner/hardware installations through the same maintained core.

- Configure company/plant identity, stations, device assignments, and supported operational variations rather than hard-coding pilot values or creating per-customer forks.
- Keep vendor protocols and SDKs behind hardware adapters that produce the shared scan contract. Replacing supported hardware should require configuration/enrollment, not domain changes.
- Make installation, configuration, upgrades, and recovery repeatable and documented. Validate supported device models, operating systems, and connection methods; arbitrary hardware is not automatically compatible.
- Deliver this incrementally using the pilot's real workflow and a simulator. Do not build a speculative universal workflow engine or every hardware adapter upfront.

## Primary users

Station behavior is configurable: dedicated operation, flexible operation selection, or a default with permitted switching. A laundry need not assign fixed operational roles to its stations. Authorized work can move to another capable station without falsifying its origin. See [station terminology and behavior](DOMAIN.md#station-identity-and-operational-purpose). This is an approved product requirement, not an implemented station-management feature.

- Plant operator: receives, sorts, processes, packs, and dispatches laundry.
- Supervisor: manages exceptions, production flow, quality, and staffing decisions.
- Driver: performs collections, deliveries, and proof of delivery.
- Customer user: views orders, stock, deliveries, discrepancies, and documents.
- Plant administrator: configures stations, devices, workflows, and local users.
- Platform administrator: manages tenants, plants, integrations, and support access.

## Core operational journey

1. Collection or customer handoff
2. Receiving and identification
3. Sorting and classification
4. Batch or production-run assignment
5. Washing and finishing
6. Quality control, rejection, or rewash
7. Packing and dispatch verification
8. Delivery and proof of delivery
9. Reconciliation and billing

Every material transition must be traceable to time, plant, station/device, operator when applicable, and the affected physical or aggregate unit.

## Initial product boundary

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
