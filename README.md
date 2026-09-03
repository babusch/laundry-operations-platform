# Laundry Operations Platform

Documentation-first foundation for a professional, multi-plant industrial laundry system.

The working repository name is **`laundry-operations-platform`**. The eventual customer-facing product can use a different brand name without forcing a repository rename.

## Product goal

Provide reliable, traceable workflows from collection and receiving through production, packing, dispatch, delivery, and billing. Shop-floor operations must continue during an internet or cloud outage.

## Current status

The project is in **discovery and architecture validation**. This repository seed contains the agreed baseline and the order in which the system should be built. Application code has not been scaffolded yet.

Start here:

- [Project status](docs/PROJECT_STATUS.md)
- [Product and scope](docs/PRODUCT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Offline and synchronization design](docs/OFFLINE_AND_SYNC.md)
- [Domain model](docs/DOMAIN.md)
- [UI design](docs/UI_DESIGN.md)
- [Delivery roadmap](docs/ROADMAP.md)
- [Repository structure](docs/REPOSITORY_STRUCTURE.md)
- [Architecture decisions](docs/decisions/README.md)

## Architectural baseline

- Monorepo with independently deployable applications.
- React/Next.js management web application.
- React/Vite operator PWA optimized for scanning and offline use.
- ASP.NET Core on .NET 10 LTS for cloud and edge services.
- PostgreSQL for cloud and plant-level durable storage.
- Modular monolith first; extract services only for demonstrated operational reasons.
- A local plant gateway isolates hardware and keeps production running offline.
- Immutable, idempotent operational events synchronize between plant and cloud.

## For AI agents

Read [AGENTS.md](AGENTS.md) before changing the repository. It contains the working agreement and directs agents to the relevant canonical documents. UI work additionally requires the repository's `apple-design` skill. Do not treat chat history as the source of truth once a decision is recorded here.
