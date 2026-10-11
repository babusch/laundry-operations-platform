# Development management company/plant assignment

Date: 2026-10-11.
Status: membership storage and read-only authenticated access check implemented. The user successfully applied the live Development migration, created the audited demo assignment, and verified its read-only browser response on 2026-10-11. Order access remains disabled.

## This small slice

Connect an already verified human login to explicit, application-owned company/plant permissions. The initial development assignment for `management-demo` grants only `orders.read`, `orders.create`, and `orders.plan.edit` for the synthetic tenant/plant already used by the gateway:

- Tenant/company: `11111111-1111-4111-8111-111111111111`.
- Plant: `22222222-2222-4222-8222-222222222222`.

These are fixed Development scope identifiers, not real customer data. This slice does not introduce a tenant/plant configuration UI or master-data catalog. Later customer/plant eligibility must validate real catalog records before order routes are enabled; a scope ID alone is not proof that a plant/customer is configured for service.

Login does not automatically create an assignment. Keycloak roles, email, username, and caller-provided actor fields cannot grant application plant access. The account is matched by its stable issuer plus subject from the protected management cookie, not by the mutable username. The setup helper resolves the existing `management-demo` user's non-secret ID once and stores that exact identity in the application database. No password/client secret is retrieved by this helper.

## Application-owned storage

The cloud identity/access module owns three PostgreSQL tables in `identity_access`, with its separate `ManagementAccessDbContext` and `__management_access_migrations` history table:

| Table | Purpose |
|---|---|
| `plant_memberships` | One uniquely scoped issuer/subject/tenant/plant assignment, active flag, version, creation time |
| `plant_membership_permissions` | Exact approved permission names attached by foreign key to that assignment |
| `membership_audit_entries` | Append-only initial provisioning history, recording local Development setup provenance, time, version, and resulting scope/permissions |

This is current relational access state plus audit evidence, not event sourcing. Unapproved permissions and orphan permission rows fail database constraints. Audit updates, deletes, and truncation are rejected by database triggers. Privileged database owners can bypass such protections; this is not tamper-proof storage.

Development provisioning is an explicit local command with access to the development database. It records the local OS username as `local-development-setup` provenance, not a fabricated Keycloak administrator identity. It grants no general administrative capability and is not a production provisioning workflow. Membership and permissions plus their initial audit commit together. Concurrent/repeated setup converges on one assignment; existing membership, modified permissions, or deactivation are never overwritten/reactivated by re-running setup.

Generic assignment editing, revocation UI, and attributable human administrative change commands are not implemented. Tests simulate later privileged changes to verify fresh authorization reads; they do not approve unaudited production changes. Application code must use audited, versioned administrative commands when those features are built.

## Verified-session integration

The actor resolver accepts only a successful management-cookie authentication result with the expected issuer, one stable subject, one identity, and server-owned interactive-login provenance. It rejects gateway/other schemes, missing or ambiguous identity claims, and mismatched issuers. No actor is constructed from request JSON, query parameters, or unchecked bearer claims.

Integration correction: OpenID Connect's default claim actions delete `iss` after token validation ([framework source](https://github.com/dotnet/aspnetcore/blob/v10.0.11/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectOptions.cs)). Because this application explicitly reconstructs a minimal validated session identity, it clears subsequent claim actions to preserve that identity and prevent profile remapping. Token/protocol validation is unchanged. A regression test carries a real OIDC-created cookie through the actor resolver and read-only membership endpoint against PostgreSQL, rather than testing only manually constructed cookie fixtures. Cookies created before this correction require a fresh login, not a new account or grant.

Permission checks query current application storage without an in-memory grant cache. A known permission must belong to that person's active assignment for that exact tenant/plant. Permission removal/deactivation affects the next lookup. A database outage gives no fallback grant and produces safe `management_access_unavailable` feedback.

The new read-only `/management/access` route returns only the signed-in person's active assignments (scope IDs and permissions), not raw issuer/subject, credentials, or other users' assignments. A signed-in but unassigned person gets an empty list. Anonymous callers get 401. It remains subject to the existing Development-only, exact loopback HTTPS boundary and no-store caching policy.

The route is opt-in through `ManagementAccess:Enabled`; normal startup leaves it disabled. `/management/session` continues to report `orderAccessEnabled: false`, even after membership assignment. Order routes/storage access remain disconnected until real customer eligibility is implemented. A stored permission is not itself an enabled endpoint.

## User setup: one command before restarting

1. Leave Docker Desktop and the identity containers running.
2. In the terminal running the cloud API, press **Ctrl+C**. This avoids Windows file locks while the setup command builds the application.
3. From the repository root, run:

```powershell
.\deploy\local\Initialize-DevelopmentManagementAccess.ps1
```

The helper checks that exactly one enabled, non-service `management-demo` account exists in `laundry-development`, without printing its ID. It starts the existing cloud PostgreSQL container and waits for readiness, explicitly applies only the management-access migration, and runs the Development-only provisioning command. It uses the committed disposable cloud connection defaults, not private `.env` values. Compose may consume `.env` internally for normal execution; the helper does not inspect its contents.

No identity database volume or existing scan/order records are reset. Startup never automatically migrates schemas. The .NET bootstrap refuses to run outside Development or with a missing/invalid subject, and refuses pending migrations. This helper is for the default local database setup only; a custom connection/deployment needs separate reviewed commands rather than guessed credential changes.

Expected result:

```text
Development management assignment created with audit history.
Development membership setup finished. No password, client secret, or user ID was printed.
```

Repeated setup reports that the assignment already exists and makes no changes.

## User verification: restart with access explicitly enabled

After setup succeeds:

```powershell
.\deploy\local\Start-DevelopmentManagement.ps1 -WithAccess
```

The helper loads the saved `MANAGEMENT_CLIENT_SECRET` process/private `.env` setting or uses a hidden prompt when absent, then verifies it with Keycloak before starting. `-PromptForSecret` deliberately overrides saved values. See [secret setup](MANAGEMENT_SIGN_IN_SETUP.md#4-save-the-client-secret-once-and-start-the-cloud-api). `-WithAccess` enables only the read-only assignment check; it does not enable order writes. The flag and all previous process settings are restored on exit. Without the flag, `/management/access` remains unavailable and existing login behavior is unchanged.

Sign in at `https://localhost:7100/management/login`, then open `https://localhost:7100/management/access`. Expect one assignment for the synthetic tenant/plant with the three approved permissions and `orderAccessEnabled: false`.

Do not edit application memberships in Keycloak: identity and application-owned authorization remain separate. Do not paste cookies or secrets into chat; the safe assignment response contains no private credential.

## Verification and next boundary

Automated tests cover protected-session actor resolution; denial for other schemes/identities/scopes; durable, idempotent and concurrent provisioning; exact permission constraints; permission removal/deactivation and no reactivation; transaction rollback; append-only audit history; migrations; authenticated own-assignment reads; disabled route behavior; and fail-closed database outages.

All 283 .NET tests pass, including 18 new membership tests. Verification builds use separate ignored output folders so a running Windows API is not overwritten. The explicit provisioning command was also checked in Production mode and refused before database access. This does not claim live Development assignment has been run.

Synthetic helper tests can be run without Docker or private credentials:

```powershell
powershell.exe -NoProfile -File tests/management/Test-DevelopmentManagementHelper.ps1
powershell.exe -NoProfile -File tests/management/Test-DevelopmentManagementAccessSetup.ps1
```

The same two scripts can be run with `pwsh` for PowerShell 7 verification.

The management startup helper's default/`-WithAccess` modes and failed preflight paths are tested with synthetic inputs on Windows PowerShell 5.1 and PowerShell 7. No real account assignment, development migration, or real read-only browser check is claimed until the user runs the setup and verifies its result.

Live progress update: the user has now successfully run the Development migration and audited assignment setup. The read-only browser check exposed the issuer-claim cleanup bug described above; after its correction, all 284 .NET tests pass, including real-handler login-to-assignment retrieval. Browser re-verification with a fresh cookie remains pending. No reassignment or database reset is needed.

The next slice is actual customer records and plant-service eligibility, then a session-bound `IOrderPlanningAccess` adapter and reviewed order routes. Company/plant catalogs, customer administration, permission administration, production rollout, offline operator authorization, and scheduling are not silently implemented by this setup.
