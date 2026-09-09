# Local identity service — first setup

This is an optional development dependency, not employee login or authenticated scan delivery yet. Run commands in PowerShell from the repository root with Docker Desktop running. See [ADR 0010](decisions/0010-local-keycloak-development.md).

## 1. Prepare trusted HTTPS

```powershell
New-Item -ItemType Directory -Force .docker/identity/tls
dotnet dev-certs https --trust
dotnet dev-certs https --export-path .docker/identity/tls/localhost.pem --format PEM --no-password
```

Accept the Windows trust prompt only for the local ASP.NET Core development certificate you just requested. This lets your browser and .NET verify localhost HTTPS without bypassing certificate checks. Export creates `localhost.pem` and `localhost.key`; the private key is unencrypted for this local container. The entire `.docker` directory is ignored by Git. Keep it private, never share or deploy it, and do not use `dev-certs --clean` as a routine troubleshooting step (it affects other projects).

Check each command succeeded before continuing. If the certificate expires, repeat trust/export and restart Keycloak. Do not click through browser certificate warnings or disable validation.

## 2. Supply local-only passwords

Recommended for repeatable local startup: create a private `.env` in the repository root using your editor. Keep the original database password if the identity volume already exists. Save two different generated development passwords in your password manager, then replace these placeholders in the private file only:

```dotenv
IDENTITY_DB_PASSWORD='your-existing-development-database-password'
IDENTITY_ADMIN_PASSWORD='your-development-administrator-password'
```

Do not put actual passwords into `.env.example` or commit `.env`. Single-quoted dotenv values preserve literal dollar signs; a password containing a single quote requires dotenv escaping. Keep the file private. Explicitly authorized development troubleshooting may inspect credentials under AGENTS.md, but must not publish them.

Docker Compose reads `.env` automatically from this project directory. Existing PowerShell environment settings override it; clear stale overrides before using the file:

```powershell
Remove-Item Env:IDENTITY_DB_PASSWORD, Env:IDENTITY_ADMIN_PASSWORD -ErrorAction SilentlyContinue
```

Alternatively, for session-only settings instead of `.env`, use these prompts:

```powershell
$env:IDENTITY_DB_PASSWORD = [System.Net.NetworkCredential]::new('', (Read-Host 'Choose identity database password (save in your password manager)' -AsSecureString)).Password
$env:IDENTITY_ADMIN_PASSWORD = [System.Net.NetworkCredential]::new('', (Read-Host 'Choose local-admin bootstrap password (save in your password manager)' -AsSecureString)).Password
```

Choose distinct development-only passwords and save them. These prompts do not put passwords in shell history. Environment variables are still available to Docker administrators: this is not production secret management. Do not share `docker inspect` or expanded Compose configuration, which can expose them.

The database password must stay consistent with its existing volume. Changing the environment does not change an existing database password. The bootstrap administrator is created on first initialization; changing its environment value does not reset its password. Keycloak marks it as temporary; use its administration console to establish a regular administrator and retire bootstrap access before extending this beyond disposable local evaluation.

## 3. Start the optional identity stack

```powershell
docker compose -f docker-compose.identity.yml config --quiet
docker compose -f docker-compose.identity.yml up --detach
docker compose -f docker-compose.identity.yml ps
```

`-f` selects the identity file instead of the existing laundry Compose file. This starts Keycloak and its own PostgreSQL container; only Keycloak's HTTPS port is published, on this PC only. The separate named volume preserves identity data when containers stop or are replaced. Neither laundry database is changed.

The first start downloads images and initializes Keycloak's own database. `up --detach` returns before Keycloak is ready. Wait until its logs report startup, then verify:

```powershell
docker compose -f docker-compose.identity.yml logs --tail 30 keycloak
./deploy/local/Test-Identity.ps1
```

The test checks discovery metadata and public signing keys over normally validated HTTPS. If startup is still in progress, wait briefly and repeat. A passing test does not yet prove gateway token issuance or authorization.

Open [local Keycloak administration](https://localhost:8443/admin/) and sign in as `local-admin` with the bootstrap password you chose. Do not enter real employee/customer accounts. Do not create a gateway client yet; that is the next slice.

Verified on 2026-09-09: trusted HTTPS discovery, signing-key publication, and interactive `local-admin` login all succeeded.

## Stop and restart without losing data

With the private `.env` present (or saved passwords supplied as session variables):

```powershell
docker compose -f docker-compose.identity.yml stop
docker compose -f docker-compose.identity.yml start
./deploy/local/Test-Identity.ps1
```

Repeat the test after startup completes. Never add `--volumes` to a removal command unless you intentionally want to erase identity data. Closing the terminal clears its environment variables but does not stop containers. Existing default `docker compose` commands still operate only on the laundry stack.

## Limits of this checkpoint

### Troubleshooting database password failures

If logs report `password authentication failed for user "keycloak"`, changing `.env` or recreating the container does not update the role password in an existing PostgreSQL volume. Diagnose before resetting anything. An explicitly authorized password alignment can preserve the identity database; do not delete volumes as the default fix. Local socket and loopback connections in this development image use trust authentication, so a successful local `psql` call does not prove a password is correct. Test over the container network with password authentication instead.

No production hosting decision, cloud subscription, gateway registration, application realm, employee login UI, or permission changes are included. This identity service is not needed by current local scanning. Future application clients will use a separate realm, not the administrative `master` realm tested here.
