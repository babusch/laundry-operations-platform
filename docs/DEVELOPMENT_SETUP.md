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

The repository now pins its language toolchain and defines its workspace and cross-platform file conventions. The next small checkpoint can create the empty .NET solution and Docker Compose configuration before any application projects are scaffolded.

## Local PostgreSQL

The root `docker-compose.yml` defines one PostgreSQL development service. It uses a named Docker volume, so the database remains intact when its container is stopped or replaced. PostgreSQL listens on `localhost:5432` by default.

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

## Empty .NET solution

`LaundryOperations.sln` is the root solution that will contain the cloud API, background worker, edge gateway, and their .NET tests. It is intentionally empty at this checkpoint.

Verify it with:

```powershell
dotnet sln LaundryOperations.sln list
```

## Cloud API health check

`apps/cloud/Laundry.Api` is the first runnable application. At this checkpoint it exposes only `GET /health`; no business or database behavior has been added.

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
