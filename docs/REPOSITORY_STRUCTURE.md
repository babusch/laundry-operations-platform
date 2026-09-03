# Repository structure

## Target layout

```text
laundry-operations-platform/
├─ apps/
│  ├─ admin-web/                 Next.js management application
│  ├─ customer-portal/           Add separately only when justified
│  ├─ station-pwa/               React/Vite operator application
│  ├─ cloud/
│  │  ├─ Laundry.Api/            ASP.NET Core API and SignalR host
│  │  ├─ Laundry.Worker/         Durable asynchronous processing
│  │  └─ Laundry.Modules/        Modular business capabilities
│  ├─ edge/
│  │  ├─ Laundry.Edge/           Gateway host and synchronization
│  │  └─ Laundry.Edge.Adapters/  Hardware/protocol adapters
│  └─ device-simulator/          Development and automated-test devices
├─ packages/
│  ├─ ui/                        Shared presentation components
│  ├─ api-client/                Generated TypeScript client
│  ├─ station-components/        Scan and offline UI primitives
│  └─ contracts/                 Stable versioned event schemas
├─ tests/
│  ├─ end-to-end/
│  ├─ integration/
│  ├─ synchronization/
│  └─ resilience/
├─ deploy/
│  ├─ local/
│  ├─ cloud/
│  ├─ edge/
│  └─ terraform/
├─ docs/
│  └─ decisions/
├─ LaundryOperations.sln
├─ pnpm-workspace.yaml
├─ docker-compose.yml
└─ AGENTS.md
```

## Repository conventions

- Use one Git repository initially.
- Use trunk-based development with short-lived branches and protected `main`.
- Keep applications independently deployable even though their sources are co-located.
- Generate browser clients from OpenAPI; never maintain handwritten duplicate DTOs.
- Version cloud-edge event schemas and test backward/forward compatibility.
- Keep module tests near modules; reserve root `tests/` for cross-component behavior.
- Put development orchestration at the root so a new contributor has one documented setup path.
- Commit repeatable migrations and infrastructure definitions.
- Keep generated build outputs, secrets, local databases, and device captures containing real data out of Git.

## When to split repositories

Do not split merely because components deploy separately. Consider a split only when there is separate ownership, security governance, release control, or a hard distribution boundary. The most likely future split is:

```text
laundry-operations-platform     Cloud, web apps, schemas, and product documentation
laundry-edge                    Gateway and hardware adapters
laundry-infrastructure          Only if infrastructure has separate ownership
```

