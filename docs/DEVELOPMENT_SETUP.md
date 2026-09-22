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

Optional identity development now has a separate [Keycloak setup guide](LOCAL_IDENTITY_SETUP.md). Its explicit `docker-compose.identity.yml` commands do not change the default laundry stack below.

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

While it is running, open `https://localhost:7100/health` in a browser or check it from another PowerShell window:

```powershell
Invoke-RestMethod https://localhost:7100/health
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
5. Start the API on this computer at `https://localhost:7100` using the trusted development certificate prepared for local identity setup.

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
Invoke-RestMethod https://localhost:7100/health/ready
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

## Submit an authenticated simulated scan to the cloud

Complete [local identity setup](LOCAL_IDENTITY_SETUP.md) first. With Docker running, apply pending migrations before starting the API:

```powershell
dotnet ef database update --project apps/cloud/Laundry.Api -- --environment Development
dotnet run --project apps/cloud/Laundry.Api
```

In another terminal at the repository root, run the non-disclosing authentication smoke test:

```powershell
./deploy/local/Test-Cloud-Gateway-Authentication.ps1
```

The script obtains a short-lived gateway token without printing it. It verifies 401 without credentials, 403 for a different plant, 201 for a permitted observation, and 200 with the unchanged original receipt on retry. It prints no credential, token, or scan identifier.

| HTTP status | Meaning |
|---|---|
| 201 | A new observation was durably stored; status is `accepted` |
| 200 | The same ID and payload were already stored; status is `alreadyProcessed` |
| 401 | Gateway token is missing, invalid, expired, or intended for another issuer/API |
| 400 | Invalid JSON, contract violation, or unsupported storage representation |
| 403 | Authenticated gateway lacks permission, has invalid registration claims, is not local in this development slice, or its tenant/plant differs from the event |
| 409 | ID conflicts with an existing observation; nothing is overwritten |
| 413 | Body exceeds 16 KiB |
| 415 | Body is not JSON |
| 503 | Storage unavailable; retain the event and retry unchanged (`Retry-After: 5`) |

The route is enabled only in Development with `ScanIngestion:Enabled` and accepts loopback connections only. ASP.NET Core validates Keycloak signature metadata, issuer, `laundry-cloud-api` audience, expiry, and the `scans.ingest` permission. Verified token claims supply the registered tenant/plant and must match the event. In Staging and Production the route still returns 404 even if the flag is set; production gateway access is not enabled.

UUIDs and timestamps are checked against the embedded JSON Schema. Unknown item identifiers are accepted without item resolution. The accepted payload is immutable: changing any field while reusing its ID produces a conflict. JSON property order and insignificant whitespace do not matter. Existing rows from before payload preservation return 409 on replay because exact equality cannot be established.

For a new physical observation, generate a new `eventId`. For a transport retry, keep all fields unchanged. Source time can be wrong and delivery can be delayed or out of order; these facts are retained. Clients must not assume a 503 or lost response means nothing was committed.

The ingestion tests exercise the real HTTP boundary against isolated PostgreSQL containers. See [ADR 0004](decisions/0004-bootstrap-local-scan-ingestion.md) for the development access boundary and retry policy.

## Run the local gateway

The gateway provides health checks, durable local scan acceptance, and authenticated background outbox forwarding. The cloud API and Keycloak do not need to run for local acceptance. Checked-in forwarding defaults to disabled so the basic gateway can start without a private credential or cloud dependency; accepted events remain durable locally.

With Docker Desktop running, open a terminal at the repository root:

```powershell
docker compose up --detach --wait plant-postgres
dotnet tool restore
dotnet ef database update --project apps/edge/Laundry.Edge -- --environment Development
dotnet run --project apps/edge/Laundry.Edge
```

The first command starts only the plant database. `--detach` leaves the container running in the background; `--wait` waits for its health check. The next commands restore the repository's migration tool and apply the gateway migrations, creating its durable observations, outbox, replay audit, and trusted-source security foundation. Repeating the migration command preserves data. The trusted-source migration creates no browser enrollment or reusable credential. The final command starts the gateway primarily on trusted `https://localhost:7200`, with cloud forwarding off, and keeps that terminal occupied. Startup does not apply migrations automatically.

The Development profile also retains `http://localhost:5200` temporarily so existing loopback-only work is not broken while station enrollment is built. Both addresses bind to `localhost`; neither is a LAN listener. New station-facing commands and code must use HTTPS. The HTTP address is not the permanent security model and will be removed after the protected station route has replaced it. If HTTPS reports a certificate trust error, create and trust the ASP.NET development certificate once with `dotnet dev-certs https --trust`, accept the Windows trust prompt, stop the gateway, and start it again. Never use that development certificate as a plant or production certificate.

### Verify Development browser-source enrollment

With the migrated plant database and gateway running over trusted HTTPS, use another terminal:

```powershell
./deploy/local/Test-Gateway-Source-Enrollment.ps1
```

The helper creates one pending browser trusted source for the configured Development tenant, plant, and station; receives a ten-minute single-use enrollment code; exchanges it over HTTPS for a 30-day Development credential in a host-only `Secure`, `HttpOnly`, `SameSite=Strict` cookie; obtains an antiforgery token; and submits one synthetic scan twice. It proves the protected mutation requires the cookie and token, the first submission is durable, and the unchanged retry is idempotent. The helper does not print the code, cookie, token, tag value, or generated IDs; normal gateway logs still include synthetic event and correlation IDs for traceability. A successful run leaves one active synthetic browser source, one observation/outbox pair, and only credential/code digests in plant PostgreSQL.

These ten-minute and 30-day values are Development proof settings, not approved production lifetimes. The bootstrap route exists only in Development, requires loopback HTTPS, and is attributed to `local-development-bootstrap`; it is not a production administrative mechanism. Enrollment, session, and scan submission reject HTTP. `POST /api/scans` now requires the enrolled source cookie, `scans.submit`, and the antiforgery token. This authenticates the source/station only; authenticated operator and workflow context must be added before a scan can count as a real laundry operation.

### Verify restart, offline independence, and local revocation

Stop any gateway already using port 7200, keep `plant-postgres` running, and run:

```powershell
./deploy/local/Test-Gateway-Source-Resilience.ps1
```

The helper starts the Development gateway with its checked-in cloud forwarding default disabled, enrolls one synthetic browser source, and durably accepts a synthetic observation without requiring cloud or Keycloak. It restarts the gateway process, obtains a fresh antiforgery token using the same persisted source cookie, and proves an unchanged retry remains one observation/outbox pair. It then revokes only the newly created synthetic source in plant PostgreSQL, verifies another scan returns 401 and is not stored, stops the gateway, and removes its temporary logs. It leaves the synthetic source revoked and the first synthetic observation queued in the Development database.

The browser cookie is an opaque bearer credential. Copying it to another tenant/plant gateway fails trusted-scope resolution, but an exact copy presented to the same gateway is indistinguishable from the original browser until it expires or is revoked. A pilot that requires device-bound copy resistance should use a managed certificate or another platform-supported device-bound credential rather than inventing a browser signing protocol.

In another terminal:

```powershell
Invoke-RestMethod https://localhost:7200/health
Invoke-RestMethod https://localhost:7200/health/ready
```

Both should return `Healthy`:

| Endpoint | What it proves |
|---|---|
| `/health` | The gateway process can answer HTTP requests. |
| `/health/ready` | The gateway can connect to its plant PostgreSQL database. |

Readiness does not yet prove write capacity, correct migrations, or scan acceptance. It deliberately does not check the cloud. Missing or unavailable database configuration produces HTTP 503 with `Unhealthy`; liveness remains healthy.

### Run the station Development simulator

For fast visual development, keep the plant database and gateway running, then open another terminal at the repository root:

```powershell
corepack pnpm dev:station
```

Open `http://127.0.0.1:5173`. Vite forwards the same `/api` and `/health` paths used by the deployed application to `https://localhost:7200`. The station shell shows checking, ready, or unavailable and provides a manual retry after failure. This HTTP mode is for rapid visual development, not secure-cookie enrollment verification.

For the same-origin HTTPS integration mode, stop Vite if it is running, then build the station application before starting or restarting the gateway:

```powershell
corepack pnpm build:station
dotnet run --project apps/edge/Laundry.Edge
```

Open `https://localhost:7200`. The build command writes generated files to the gateway's ignored `wwwroot` directory. The gateway serves the PWA, `/api`, and `/health` from one HTTPS origin; unknown API-like routes remain 404 responses rather than returning the PWA shell. Rebuild after frontend changes. "Gateway ready" means only that the local gateway can reach plant PostgreSQL; it does not claim cloud connectivity, current migrations, write capacity, source enrollment, or scan acceptance.

This slice sends no scans and stores no emergency queue. Source enrollment will use the HTTPS integration mode in a later reviewed step.

The Development connection string `ConnectionStrings:Plant` uses database `laundry_plant`, user `laundry_edge`, local-only password `laundry_edge_local_dev_only`, and port 15433. Other environments must provide `ConnectionStrings__Plant`; do not expose this unauthenticated foundation over the network. The launch profile binds to localhost. If you override `PLANT_POSTGRES_*` in `.env`, also supply a matching `ConnectionStrings__Plant` in the gateway terminal: ASP.NET Core does not automatically read `.env`.

To try a plant database outage while the gateway remains running:

```powershell
docker compose stop plant-postgres
Invoke-RestMethod https://localhost:7200/health
Invoke-WebRequest https://localhost:7200/health/ready -SkipHttpErrorCheck
docker compose up --detach --wait plant-postgres
Invoke-RestMethod https://localhost:7200/health/ready
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

With the migration applied and gateway running, use the verified Development helper:

```powershell
./deploy/local/Test-Gateway-Source-Enrollment.ps1
```

The helper enrolls a simulated browser source, submits a fresh barcode observation, and retries the identical submission. It verifies `acceptedLocally` followed by `alreadyAcceptedLocally` without printing identifiers or credential material. When authenticated forwarding is enabled, the background worker may change the delivery status to `synchronized` quickly. Local acceptance means saved at the plant, not an operator-approved business action. The RFID request shape is also covered by automated tests.

The gateway transaction saves the scan plus an outbox entry (a durable to-do item for cloud delivery). If either write fails, neither commits. A background worker delivers stored events without changing their payloads and records confirmed cloud receipts. Application and database restarts preserve these records.

The Development-only endpoint requires loopback HTTPS, an active enrolled source cookie, `scans.submit`, and the matching antiforgery token. It accepts only `submit-scan.v2`, which contains origin observation data but no tenant, plant, station, source, device, or gateway-time claims. The gateway adds trusted tenant/plant/station/source attribution from the source record and produces `scan.observed.v2`. It rejects caller-supplied gateway-owned fields (400), conflicting event-ID reuse—including reuse from another source—(409), oversized bodies (413), and non-JSON content (415). A storage or authentication-database error returns 503 with a retry hint; keep the same submission because the commit outcome may be uncertain. The endpoint remains loopback-only while production enrollment, operator sessions, and plant certificate deployment are designed.

Inspect pending delivery bookkeeping without printing tag values:

```powershell
docker compose exec plant-postgres psql --username laundry_edge --dbname laundry_plant --command 'SELECT event_id, status FROM plant.outbox;'
```

For another fresh protected synthetic observation, rerun the helper. It assigns one new ID and preserves it for its retry:

```powershell
./deploy/local/Test-Gateway-Source-Enrollment.ps1
```

## Run authenticated gateway-to-cloud forwarding

Complete [local identity setup](LOCAL_IDENTITY_SETUP.md) first. Start both databases and Keycloak, then apply the existing migrations:

```powershell
docker compose up --detach --wait postgres plant-postgres
docker compose -f docker-compose.identity.yml up --detach --wait
./deploy/local/Initialize-Gateway-Identity.ps1
dotnet ef database update --project apps/cloud/Laundry.Api -- --environment Development
dotnet ef database update --project apps/edge/Laundry.Edge -- --environment Development
```

Open a second terminal at the repository root and start the protected cloud API:

```powershell
dotnet run --project apps/cloud/Laundry.Api
```

Open a third terminal and start the gateway with authenticated forwarding:

```powershell
./deploy/local/Start-DevelopmentGateway.ps1
```

The helper asks Docker Compose to resolve the existing ignored `.env` configuration, passes only `GATEWAY_CLIENT_SECRET` to the gateway child process as `GatewayIdentity__ClientSecret`, and enables forwarding for that process. It does not print the value. The token is requested with the client-credentials grant, kept only in memory, shared between deliveries, and refreshed before expiration. Press **Ctrl+C** to stop the gateway; the helper removes or restores its temporary process settings.

Do not replace the helper with a checked-in secret. Ordinary `dotnet run --project apps/edge/Laundry.Edge` remains useful when developing local acceptance without identity or cloud services.

With all three services running, use a fourth terminal for the non-disclosing end-to-end test:

```powershell
./deploy/local/Test-Gateway-Authenticated-Forwarding.ps1
```

The script creates one synthetic observation, submits it to the gateway, and waits up to 30 seconds for a confirmed cloud receipt. It prints no credential, token, tag value, or generated scan identifier. A successful run adds that synthetic observation to both development databases.

Inspect delivery state without printing payloads:

```powershell
docker compose exec plant-postgres psql --username laundry_edge --dbname laundry_plant --command 'SELECT event_id, status, attempts, next_attempt_at_utc, cloud_received_at_utc, last_error FROM plant.outbox;'
```

| Status | Meaning |
|---|---|
| `pending` | Saved locally; awaiting a confirmed cloud receipt, possibly retrying. |
| `synchronized` | Cloud returned a validated receipt for this event; its receipt time is saved. |
| `needsAttention` | Cloud rejected the request or configuration/access needs intervention; the event is retained. |

`Forwarding:Endpoint` is `https://localhost:7100/api/scans` and the token endpoint is the local Keycloak HTTPS endpoint. Both are validated as loopback Development addresses; redirects and proxies are disabled. The worker never runs outside Development.

The durable retry implementation remains intact: cloud and identity outages keep events `pending`, permanent event rejection becomes `needsAttention`, and restart retains the queue. If the cloud rejects a cached token with 401, the gateway invalidates it, requests a replacement, and retries once. Rejected gateway credentials remain pending with a safe diagnostic code so correcting system configuration can resume the queue without replaying every event manually.

The identity-outage recovery test can be repeated safely with an isolated disposable PostgreSQL container and simulated identity availability:

```powershell
dotnet test apps/edge/Laundry.Edge.Tests --filter "FullyQualifiedName~IdentityFailureRetainsEventUntilRecovery"
```

Live verification on 2026-09-13 also stopped the actual Keycloak container before a fresh gateway requested a token. Gateway readiness remained healthy, a synthetic scan was accepted locally and stayed pending with `identity_connection_failed`, and the same stored event synchronized automatically after Keycloak restarted. No database volume was removed.

A separate live test resolved and temporarily disabled only Keycloak's `service-account-gateway-development` account before a fresh gateway had obtained a token. Token issuance returned 401, gateway readiness and local acceptance remained healthy, and the event stayed pending with `identity_credentials_rejected`. Re-enabling that exact account drained the unchanged event automatically; the standard gateway identity test passed afterward. The test used a guaranteed restoration block and did not change the client secret, realm mappings, or database volumes.

Disabling the account prevents **new** tokens. A signed token that was already cached can remain accepted until it expires—currently no more than five minutes—because the cloud validates it locally rather than asking Keycloak about every request. This bounded delay preserves availability and avoids making every scan depend on a live identity lookup. Production revocation timing must be agreed explicitly; do not describe account disablement as instantaneous revocation.

### Verify signing-key rotation

With Keycloak and the cloud API running as described above, run:

```powershell
./deploy/local/Test-Gateway-Signing-Key-Rotation.ps1
```

The script first makes the running cloud cache the current public-key metadata. It then creates a uniquely named temporary RSA signing provider at higher priority, confirms Keycloak issues newly signed tokens while publishing both current and previous public keys, and submits synthetic observations under both keys. The first new-key request may return 401 while ASP.NET Core requests fresh metadata; retrying the unchanged event is safe and matches the gateway sender's behavior.

A `finally` block removes only the provider created by that run and confirms Keycloak resumed signing with the original development key. The check adds two synthetic observations to the cloud development database, but does not alter plant data or database volumes. It resolves existing ignored Development credentials through Docker Compose internally and does not print credentials, tokens, signing-key IDs, or generated scan IDs. Do not use this development helper against a production realm.

Run the cross-component test with Docker available:

```powershell
dotnet test tests/synchronization/Laundry.Synchronization.Tests
```

It hosts the real gateway and cloud applications with separate temporary databases and injects transport failures, including a lost response after cloud commit. It does not modify your Compose databases.

## Inspect synchronization and safely replay a failure

Apply the gateway migrations with the gateway stopped, then start it:

```powershell
dotnet ef database update --project apps/edge/Laundry.Edge -- --environment Development
dotnet run --project apps/edge/Laundry.Edge
```

The `ReplayAudit` migration adds an audit table without changing or deleting scans. In a second terminal:

```powershell
Invoke-RestMethod https://localhost:7200/api/sync/summary | ConvertTo-Json -Depth 5
Invoke-RestMethod 'https://localhost:7200/api/sync/events?status=needsAttention&limit=50' | ConvertTo-Json -Depth 5
```

The summary reports pending/synchronized/needs-attention counts, oldest pending age in seconds (null when none), last confirmed cloud receipt time, forwarding enablement, and the worker's most recent iteration. A successful iteration does not mean the cloud is reachable: examine delivery states and error codes too. Worker heartbeat information resets at restart. A database failure gives HTTP 503 rather than pretending the queue is empty. Health readiness still checks plant connectivity only.

The list includes event IDs, acceptance times, attempts, retry/lease timing, cloud receipt time, and safe error codes—not tag identifiers or scan payloads. Omit `status` for all states; `limit` is 1–100 (default 50), `offset` defaults to zero. Follow `nextOffset` until null. Lists can shift as deliveries change, so refresh a record before acting. Audit lists use pages of 50 with the same `nextOffset` convention.

For a specific event, replace the placeholder with an ID from the list:

```powershell
$eventId = 'PASTE-EVENT-ID-HERE'
$record = Invoke-RestMethod "https://localhost:7200/api/sync/events/$eventId"
$record | ConvertTo-Json
Invoke-RestMethod "https://localhost:7200/api/sync/events/$eventId/replays" | ConvertTo-Json -Depth 5
```

Review and correct the underlying issue first. Examples: `http_403` may mean mismatched source scope, `http_404` may mean the development route is disabled, and `http_409` means an event-ID conflict that resending alone will not fix. Do not change history to bypass a conflict. If the record is still `needsAttention` and a retry is justified, create one replay request:

```powershell
$replay = @{
    requestId = [guid]::NewGuid().ToString()
    expectedAttempts = $record.attempts
    reasonCode = 'configurationCorrected'
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "https://localhost:7200/api/sync/events/$eventId/replay" -ContentType application/json -Body $replay
```

Allowed reasons are `configurationCorrected`, `cloudIssueResolved`, and `reviewedForRetry`. Choose the reason that actually applies. No free-text personal information is needed.

HTTP 202 / `replayRequested` means the requeue and audit record committed together, not that the cloud accepted the scan. The sender will try the exact original event. If the response is lost, repeat the same `$replay` body with the same request ID; HTTP 200 / `replayAlreadyRequested` confirms that original command, even if delivery has since completed. A new intentional replay after another failure requires reviewing the new attempt count and generating a new request ID.

HTTP 409 means stale state, a live lease, a record not in `needsAttention`, or changed request-ID reuse. Refresh and investigate rather than blindly retrying. A missing or out-of-scope event returns 404. On 503, retain the same replay request because its commit outcome might be uncertain.

These routes are enabled only in Development with scan acceptance and `SyncDiagnostics:Enabled`, and require loopback access. The audit actor is explicitly `local-development-unattributed`, not an authenticated user. Do not expose these commands on the plant network before checkpoint 5 adds identity and authorization. No browser dashboard or bulk replay is included yet.

## Contract validation

The language-neutral event contracts under `packages/contracts` are validated with Node.js tests. Install the pinned workspace dependencies and run the contract suite:

```powershell
corepack pnpm install
corepack pnpm test:contracts
```

The first install creates or updates `pnpm-lock.yaml`. Commit that lockfile so every environment resolves the same dependency versions.

## Station application foundation

The station PWA under `apps/station-pwa` is a clearly labelled Development simulator shell with local gateway-readiness feedback. It deliberately sends no scans. Source enrollment, scan controls, service-worker installation, and browser offline storage are not implemented yet.

The typed gateway client under `packages/api-client` is generated from `apps/edge/Laundry.Edge/OpenApi/station-api.v1.yaml`. That OpenAPI document describes gateway readiness and `POST /api/scans`, and references the canonical `submit-scan.v2` JSON Schema. Regenerate it whenever the HTTP description changes:

```powershell
corepack pnpm generate:api-client
```

Run the focused checks and production build:

```powershell
corepack pnpm test:station
corepack pnpm build:station
```

To view the shell with Vite during visual development:

```powershell
corepack pnpm dev:station
```

Open the local URL printed by Vite and stop it with `Ctrl+C`. For the gateway-hosted HTTPS workflow, use the earlier [station Development simulator](#run-the-station-development-simulator) instructions.
