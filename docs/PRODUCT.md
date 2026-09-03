# Product and scope

## Vision

Create a professional industrial laundry platform that gives operators a fast and dependable shop-floor experience while providing customers and managers with accurate inventory, production, delivery, quality, and billing information.

## Primary users

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

