# Development setup

This guide describes the supported local development environment. Keep it updated whenever a required tool or the primary setup commands change.

## What each tool does

| Tool | Purpose in this repository |
|---|---|
| Git | Records changes and collaborates through GitHub |
| .NET SDK | Builds and tests the cloud API, worker, and plant gateway |
| Node.js | Runs the JavaScript/TypeScript build tools |
| pnpm | Installs and manages frontend packages across the monorepo |
| Docker Desktop | Runs development dependencies such as PostgreSQL in isolated containers |
| Docker Compose | Starts the project's related containers together from one configuration file |

Docker does not move the source code into the cloud. It runs local, isolated processes called **containers**. A container is created from an **image**, which is a packaged filesystem and startup definition. A **volume** retains database files when a container is replaced. Docker Compose describes several related containers, networks, and volumes in one YAML file.

Initially, Docker will run only development infrastructure such as PostgreSQL. The applications can run directly through `dotnet` and `pnpm` for a fast development loop.

## Supported Windows baseline

- 64-bit supported edition of Windows 10 or Windows 11
- WSL 2 enabled
- Git
- .NET 10 SDK
- Node.js 24 LTS
- pnpm through Corepack
- Docker Desktop using the WSL 2 backend

Multiple .NET SDK versions can be installed side by side. Installing .NET 10 does not require removing .NET 9.

## Repository-pinned versions

The initial development baseline is:

| Tool | Project version | Where it is recorded |
|---|---:|---|
| .NET SDK | 10.0.400 | `global.json` |
| Node.js | 24.20.0 | `.node-version` and `package.json` |
| pnpm | 11.25.0 | `package.json` |

An exact .NET SDK and pnpm version make local and CI builds repeatable. The Node.js engine accepts compatible Node 24 releases, while `.node-version` records the version used to establish the project.

## Install Docker Desktop

1. Open the official [Docker Desktop installation guide for Windows](https://docs.docker.com/desktop/setup/install/windows-install/).
2. Download Docker Desktop for Windows for the machine's architecture. Most Windows PCs use x86_64/AMD64.
3. Choose the recommended per-user installation unless the machine is centrally administered.
4. Use the WSL 2 backend when prompted.
5. Start Docker Desktop and wait until it reports that the engine is running.
6. In Docker Desktop settings, keep **Use the WSL 2 based engine** enabled.

Verify in a new PowerShell window:

```powershell
docker --version
docker compose version
docker run --rm hello-world
```

The final command downloads a tiny test image, runs it, prints a confirmation, and removes the test container. It does not remove the downloaded image.

## Install the .NET 10 SDK

1. Open Microsoft's official [Install .NET on Windows](https://learn.microsoft.com/dotnet/core/install/windows) guide.
2. Select the .NET 10 **SDK** Windows installer for the machine's architecture. Install the SDK, not only a runtime.
3. Complete the installer and open a new PowerShell window.

Verify:

```powershell
dotnet --list-sdks
```

The output should include a version beginning with `10.0.`. The SDK includes the corresponding ASP.NET Core and .NET runtimes needed for development.

## Verify Node.js and pnpm

Verify:

```powershell
node --version
corepack --version
corepack pnpm --version
```

Node.js should report a supported 24.x LTS release. Corepack reads the pnpm version from `package.json`, downloads it to a user cache when necessary, and then runs that exact version.

To make the shorter `pnpm` command available, open PowerShell **as Administrator** once and run:

```powershell
corepack enable pnpm
```

Close that elevated window, open a normal PowerShell window, and confirm that `pnpm --version` prints the project-pinned version. Administrator access is needed only to place Corepack's command shim beside Node.js under `C:\Program Files\nodejs`; day-to-day pnpm use should not be elevated.

## First-checkpoint acceptance

This setup checkpoint is complete when all of these commands succeed in a new PowerShell window:

```powershell
git --version
dotnet --version
node --version
corepack pnpm --version
docker --version
docker compose version
docker run --rm hello-world
```

The toolchain was verified on 2026-09-03 with Git 2.55.0.windows.5, .NET SDK 10.0.400, Node.js 24.20.0, Docker Desktop 4.89.0, Docker Engine 29.7.2, and Docker Compose 5.5.0. Docker also completed the `hello-world` container test.

The repository pins its language toolchain and defines its workspace and cross-platform file conventions. The .NET solution and Docker Compose configuration are now in place.

## Local PostgreSQL

The root `docker-compose.yml` defines two independent PostgreSQL development services: `postgres` for cloud data on `127.0.0.1:15432`, and `plant-postgres` for gateway data on `127.0.0.1:15433`. Each uses its own named volume, retaining its database when stopped or replaced. Both use port 5432 inside their separate containers; their host ports avoid a conflict with an existing Windows PostgreSQL service.

The checked-in values are deliberately local-development credentials. To override them, copy `.env.example` to `.env` and edit that untracked file. Never reuse these values outside local development.

Validate the resolved configuration without starting anything:

```powershell
docker compose config
```

Start PostgreSQL and wait for its health check:

```powershell
docker compose up --detach --wait
```

Inspect the running services:

```powershell
docker compose ps
```

Open a PostgreSQL prompt inside the container:

```powershell
docker compose exec postgres psql --username laundry --dbname laundry
```

Enter `\q` to leave `psql`. To inspect problems, run `docker compose logs postgres`.

Stop the service while retaining its data:

```powershell
docker compose down
```

`docker compose down --volumes` also deletes the local database volume and all data in it. Use that destructive variant only when intentionally resetting the development database.

## .NET solution

`LaundryOperations.sln` groups the cloud API, local gateway, and their separate test projects. The cloud background worker remains a future checkpoint.

Verify it with:

```powershell
dotnet sln LaundryOperations.sln list
```

## Cloud API health check

`apps/cloud/Laundry.Api` exposes `GET /health` for process health, `GET /health/ready` for PostgreSQL connectivity, and `POST /api/scans` for local development scan ingestion.

Restore packages, build the complete solution, and run all tests:

```powershell
dotnet restore LaundryOperations.sln
dotnet build LaundryOperations.sln --no-restore
dotnet test LaundryOperations.sln --no-build
```

Start the API:

```powershell
dotnet run --project apps/cloud/Laundry.Api
```

While it is running, open `http://localhost:5100/health` in a browser or check it from another PowerShell window:

```powershell
Invoke-RestMethod http://localhost:5100/health
```

The response should be `Healthy`. Return to the API terminal and press **Ctrl+C** to stop it. PostgreSQL does not need to be running for this basic process health check.

## Connect the API to PostgreSQL

Run these commands from the repository root with Docker Desktop running:

```powershell
docker compose up --detach --wait
dotnet tool restore
dotnet restore LaundryOperations.sln
dotnet ef database update --project apps/cloud/Laundry.Api -- --environment Development
dotnet run --project apps/cloud/Laundry.Api
```

The commands perform these steps:

1. Start PostgreSQL in Docker and wait until it accepts connections.
2. Install the repository-pinned `dotnet-ef` tool, used to manage database migrations.
3. Download the .NET packages, including EF Core and the Npgsql PostgreSQL provider.
4. Apply pending migrations to the local database. A migration is a version-controlled description of a database structure change. EF records applied migrations in its history table, so repeating this command preserves existing data and applies only pending changes.
5. Start the API on this computer at `http://localhost:5100`.

The API's Development configuration contains a connection string matching the Compose defaults:

| Setting | Local default | Meaning |
|---|---|---|
| Host | localhost | PostgreSQL is reached through this computer |
| Port | 15432 | The port Docker publishes on this computer |
| Database | laundry | The database inside PostgreSQL |
| Username | laundry | The local development database user |
| Password | laundry_local_dev_only | Disposable development credential |

These defaults exist only in `appsettings.Development.json`. Other environments must supply `ConnectionStrings__Laundry` through deployment configuration. EF uses Npgsql to connect to PostgreSQL; no PostgreSQL installation on Windows is needed.

Compose reads `.env`, but ASP.NET Core does not read that file automatically. If you change the Compose database settings, provide a matching API connection string through `ConnectionStrings__Laundry` in the same terminal before running migrations or the API. Changing Compose's initialization credentials does not change users/passwords in an existing volume.

In a second terminal, verify the API can reach the database:

```powershell
Invoke-RestMethod http://localhost:5100/health/ready
```

Expect `Healthy`. If PostgreSQL stops, readiness returns HTTP 503 (`Unhealthy`) while `/health` still returns HTTP 200. Readiness currently checks connectivity only; it does not prove migrations are current.

Inspect the new table using PostgreSQL's command-line client inside Docker:

```powershell
docker compose exec postgres psql --username laundry --dbname laundry --command '\d integrations.scan_observations'
```

`integrations.scan_observations` stores the agreed observation fields and a separate cloud receipt timestamp. The nested identifier becomes two columns. The primary key on `event_id` prevents duplicate inserts, including simultaneous attempts. The `PreserveScanPayload` migration adds the original JSON for exact retry comparison without losing timestamp precision.

Migrations are applied explicitly, never automatically at API startup. Future model changes get a new migration rather than edits to an already-applied migration. See Microsoft's [migration guidance](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying).

## Database tests

With Docker Desktop running:

```powershell
dotnet test LaundryOperations.sln
```

The database tests create isolated PostgreSQL containers with random host ports, apply the real migrations, and remove those temporary containers after testing. They do not use or erase your Compose database. They test barcode/RFID storage, sequential and simultaneous duplicates, delayed observations, wrong source clocks, migration reapplication, and database outage/recovery.

To run only tests that do not need Docker:

```powershell
dotnet test LaundryOperations.sln --filter "Category!=Database"
```

Stop the API with **Ctrl+C**, and stop the development database with `docker compose stop` when finished. Its named volume keeps your data.

## Repository-local .NET tools

For gateway startup and its explicit migration command, see [Run the local gateway](#run-the-local-gateway) below.

`.config/dotnet-tools.json` is a tool manifest: a list of command-line tools and the exact versions this repository uses. The directory holds configuration, not the installed tool binaries or database files.

- `dotnet-ef` at version `10.0.11` manages EF Core migrations.
- `commands` lists the command exposed by the installed tool.
- The outer `version: 1` is the manifest format version, not a .NET version.
- `isRoot: true` ends the tool-manifest search here instead of looking in parent folders.
- `rollForward: false` leaves runtime roll-forward for the tool disabled; it is distinct from the pinned tool package version.

Run `dotnet tool restore` after cloning the repository or when its tool versions change. Then `dotnet ef` uses the repository's tool. Commit the manifest so teammates and CI can restore the same version.

`global.json` selects the .NET SDK that builds the code. The `.csproj` files list application and test dependencies. The tool manifest separately selects development commands such as the migration tool.

## Submit a simulated scan

With Docker running, apply pending migrations before starting the API:

```powershell
dotnet ef database update --project apps/cloud/Laundry.Api -- --environment Development
dotnet run --project apps/cloud/Laundry.Api
```

In another terminal at the repository root:

```powershell
$scanJson = Get-Content packages/contracts/examples/scan-observed.v1.barcode.json -Raw
Invoke-RestMethod -Method Post -Uri http://localhost:5100/api/scans -ContentType application/json -Body $scanJson
```

The response contains `eventId`, `status`, and `cloudReceivedAtUtc`. Repeat the same command to see `alreadyProcessed`. The cloud receipt timestamp remains unchanged.

| HTTP status | Meaning |
|---|---|
| 201 | A new observation was durably stored; status is `accepted` |
| 200 | The same ID and payload were already stored; status is `alreadyProcessed` |
| 400 | Invalid JSON, contract violation, or unsupported storage representation |
| 403 | Request is not local or its tenant/plant differs from server configuration |
| 409 | ID conflicts with an existing observation; nothing is overwritten |
| 413 | Body exceeds 16 KiB |
| 415 | Body is not JSON |
| 503 | Storage unavailable; retain the event and retry unchanged (`Retry-After: 5`) |

The route is enabled only in Development with `ScanIngestion:Enabled`. Its server-configured tenant and plant match the synthetic examples. It accepts loopback connections only; it is not a production authentication mechanism. In Staging and Production the route returns 404 even if the configuration flag is set.

UUIDs and timestamps are checked against the embedded JSON Schema. Unknown item identifiers are accepted without item resolution. The accepted payload is immutable: changing any field while reusing its ID produces a conflict. JSON property order and insignificant whitespace do not matter. Existing rows from before payload preservation return 409 on replay because exact equality cannot be established.

For a new physical observation, generate a new `eventId`. For a transport retry, keep all fields unchanged. Source time can be wrong and delivery can be delayed or out of order; these facts are retained. Clients must not assume a 503 or lost response means nothing was committed.

The ingestion tests exercise the real HTTP boundary against isolated PostgreSQL containers. See [ADR 0004](decisions/0004-bootstrap-local-scan-ingestion.md) for the development access boundary and retry policy.

## Run the local gateway

The gateway provides health checks and durable local scan acceptance with a pending outbox. The cloud API does not need to run. Cloud forwarding is not implemented yet.

With Docker Desktop running, open a terminal at the repository root:

```powershell
docker compose up --detach --wait plant-postgres
dotnet tool restore
dotnet ef database update --project apps/edge/Laundry.Edge -- --environment Development
dotnet run --project apps/edge/Laundry.Edge
```

The first command starts only the plant database. `--detach` leaves the container running in the background; `--wait` waits for its health check. The next commands restore the repository's migration tool and apply the gateway's migration, creating `plant.observations` and `plant.outbox`. Repeating the migration command preserves data. The final command starts the gateway on `http://localhost:5200` and keeps that terminal occupied. Startup does not apply migrations automatically.

In another terminal:

```powershell
Invoke-RestMethod http://localhost:5200/health
Invoke-RestMethod http://localhost:5200/health/ready
```

Both should return `Healthy`:

| Endpoint | What it proves |
|---|---|
| `/health` | The gateway process can answer HTTP requests. |
| `/health/ready` | The gateway can connect to its plant PostgreSQL database. |

Readiness does not yet prove write capacity, correct migrations, or scan acceptance. It deliberately does not check the cloud. Missing or unavailable database configuration produces HTTP 503 with `Unhealthy`; liveness remains healthy.

The Development connection string `ConnectionStrings:Plant` uses database `laundry_plant`, user `laundry_edge`, local-only password `laundry_edge_local_dev_only`, and port 15433. Other environments must provide `ConnectionStrings__Plant`; do not expose this unauthenticated foundation over the network. The launch profile binds to localhost. If you override `PLANT_POSTGRES_*` in `.env`, also supply a matching `ConnectionStrings__Plant` in the gateway terminal: ASP.NET Core does not automatically read `.env`.

To try a plant database outage while the gateway remains running:

```powershell
docker compose stop plant-postgres
Invoke-RestMethod http://localhost:5200/health
Invoke-WebRequest http://localhost:5200/health/ready -SkipHttpErrorCheck
docker compose up --detach --wait plant-postgres
Invoke-RestMethod http://localhost:5200/health/ready
```

During the outage, expect liveness 200 and readiness 503. `-SkipHttpErrorCheck` lets PowerShell 7 display the expected failure response instead of throwing. After recovery, readiness returns `Healthy` without restarting the gateway. The named volume is preserved.

Inspect the database/container if needed:

```powershell
docker compose ps
docker compose logs plant-postgres
docker compose exec plant-postgres psql --username laundry_edge --dbname laundry_plant
```

Use `\q` to exit psql. Stop the gateway with **Ctrl+C** in its terminal, then `docker compose stop plant-postgres` to stop only its database. `docker compose down` affects both development database containers; never add `--volumes` unless deliberately deleting their data.

Run the gateway tests with Docker available:

```powershell
dotnet test apps/edge/Laundry.Edge.Tests
```

Database tests use disposable isolated PostgreSQL containers, not your development database, and run without a cloud service. Contract and non-database health tests can run with `--filter "Category!=Database"`.

### Submit a scan to the gateway

With the migration applied and gateway running, use another terminal:

```powershell
$submission = Get-Content packages/contracts/examples/submit-scan.v1.barcode.json -Raw
Invoke-RestMethod -Method Post -Uri http://localhost:5200/api/scans -ContentType application/json -Body $submission
```

Expect `status: acceptedLocally`, a gateway acceptance timestamp, and `deliveryStatus: pending`. Repeat the same command to see `alreadyAcceptedLocally` with the original timestamp. Both mean saved at the plant, not synchronized or a business action approved. The RFID submission example works with the same default simulator scope.

The gateway transaction saves the scan plus an outbox entry (a durable to-do item for future cloud delivery). If either write fails, neither commits. All outbox entries remain pending until we implement forwarding. Application and database restarts preserve these records.

The Development-only endpoint checks loopback access and the configured `ScanAcceptance` tenant/plant/station/device tuple. It rejects caller-supplied gateway timestamps, source mismatches (403), invalid submissions (400), conflicting ID reuse (409), oversized bodies (413), and non-JSON content (415). A storage error returns 503 with a retry hint; keep the same submission because the commit outcome may be uncertain. Do not expose this endpoint to other machines before authentication is implemented.

Inspect pending delivery bookkeeping without printing tag values:

```powershell
docker compose exec plant-postgres psql --username laundry_edge --dbname laundry_plant --command 'SELECT event_id, status FROM plant.outbox;'
```

For a fresh synthetic observation, assign a new ID once and then preserve it for retries:

```powershell
$newScan = Get-Content packages/contracts/examples/submit-scan.v1.barcode.json -Raw | ConvertFrom-Json
$newScan.eventId = [guid]::NewGuid().ToString()
$newScan.correlationId = $newScan.eventId
$submission = $newScan | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri http://localhost:5200/api/scans -ContentType application/json -Body $submission
```

## Contract validation

The language-neutral event contracts under `packages/contracts` are validated with Node.js tests. Install the pinned workspace dependencies and run the contract suite:

```powershell
corepack pnpm install
corepack pnpm test:contracts
```

The first install creates or updates `pnpm-lock.yaml`. Commit that lockfile so every environment resolves the same dependency versions.
