# Development customer and plant-service eligibility

Date: 2026-10-11.
Status: storage, eligibility checks, and opt-in read-only customer lookup implemented. The user successfully applied the live Development migration and audited demo-customer/service setup on 2026-10-11. Browser verification remains pending.

## Small slice and boundaries

Add one synthetic laundry customer to the existing development company and explicitly configure the development plant to serve it. Customer ownership and plant-service eligibility are distinct. Being signed in or knowing a customer ID does not establish either.

The customer's initial fields are immutable internal UUID, owning tenant/company UUID, human-readable code, display name, active flag, version, and UTC creation time. The demo is `DEMO-001` / `Demo laundry customer`, ID `33333333-3333-4333-8333-333333333333`. It belongs to tenant `11111111-1111-4111-8111-111111111111` and has one active service link to plant `22222222-2222-4222-8222-222222222222`.

All data is synthetic. No real customer names, personal information, contracts, addresses, contact details, RFID/barcode identities, or credentials are added. No new dependency, service, or datastore is introduced.

This is not customer administration, billing, a customer portal, or operational order processing. Continuous tracking/non-billable customers retain their approved product direction but their settings/workflows are not implemented in this slice. Customer lifecycle editing, rename/reassignment, generic service-link administration, scheduling, and authorized audit-based updates remain future work.

## Module-owned storage and validation

The cloud Customers module owns `CustomerDbContext`, migrations, and three PostgreSQL tables in the `customers` schema. Its migration history is `customers.__customer_migrations`.

| Table | Responsibility |
|---|---|
| `laundry_customers` | Current tenant-owned customer identity/labels, active flag, version, creation time |
| `plant_services` | Explicit tenant/plant/customer service links and their active flags |
| `setup_audit_entries` | Append-only Development provisioning provenance, time, customer, and resulting customer/service state |

Customer IDs are immutable UUID primary keys; codes are separate and unique within their owning tenant. A composite tenant/customer foreign key prevents a service link from claiming a customer owned by a different tenant. Customer labels have proposed initial limits of 50 characters for code and 200 for display name; blank labels and empty tenant/plant scope are rejected by database constraints.

`ICustomerPlantEligibility` is the stable module-owned application contract. It does not expose EF persistence entities or authenticate humans. A customer is eligible only when it belongs to the supplied tenant and both the customer and its exact plant-service link are active. An unserved same-company customer, wrong plant, wrong tenant, unknown customer, or deactivated customer/service is not eligible. Lookups read durable current state rather than caching eligibility across requests.

The Development initializer creates customer, service link, and initial audit together. Concurrent/repeated setup converges on one record/link/audit. Repeating it does not overwrite customer labels, reactivate anything, add another plant, or reuse the demo ID for another tenant. Failure rolls back the whole transaction. SQL update/delete/truncate of setup audits is rejected by migration-installed triggers; privileged database administrators can bypass such protections. Generic audited administrative editing is not implemented, and tests simulating privileged database edits do not approve unaudited production changes.

The tenant/plant IDs are the existing explicit Development scope, not a new master-data catalog. No foreign key to a nonexistent tenant/plant module is invented. Real tenant/plant catalog integration and service-link administrative authorization require a future slice before production use. This is an online management slice; it does not publish customers to the gateway or make order-backed scanning offline-ready. Existing raw local scan acceptance is unchanged.

## Read-only customer check

Opt-in Development route:

```text
GET /management/tenants/{tenantId}/plants/{plantId}/customers
```

It requires the protected human management session and a current active application-owned `orders.read` grant for that exact company and plant. This is a customer-choice preview for order planning, not a new customer-administrator permission. Anonymous/machine-only callers fail authentication; other scopes or removed read permission fail authorization. The route retains the existing exact loopback HTTPS and no-store restrictions and is not registered in Staging/Production.

The response includes only eligible customer choices (`id`, `code`, `displayName`), `truncated`, and `orderAccessEnabled: false`. It returns at most 100 choices in stable code/ID order, with an honest truncation indicator if more exist. This deliberately bounded Development preview is not a production paginated customer-management API; production UI clients need a reviewed OpenAPI/client and pagination slice.

No actor identifiers, cookies, secrets, or other companies' customer data are returned. Known storage failures return 503 with `customer_lookup_unavailable` and a safe correlation identifier; there is no permissive fallback. The signed-in person's scope is never inferred from arbitrary customer JSON or a URL alone.

The lookup is disabled by default. Order routes and writes remain absent even when it is enabled. Future order mutations must combine current human/plant authorization with customer eligibility in a coordinated durable write; separate preview lookups are not an atomic authorization guarantee for later writes.

## User setup: apply one explicit command

1. Keep Docker Desktop running.
2. Stop the cloud API with **Ctrl+C** in its terminal, avoiding Windows build-file locks.
3. From the repository root, run:

```powershell
.\deploy\local\Initialize-DevelopmentCustomer.ps1
```

The helper starts the existing cloud database and waits for readiness, explicitly applies the `CustomerDbContext` migration, and runs the Development-only seed command. It uses the committed disposable local cloud connection, not inspected `.env` values. Normal Compose execution may consume `.env` internally. Existing scans, order storage, application memberships, Keycloak users, and database volumes are not reset or rewritten.

For the seed process, the helper temporarily supplies the same committed local connection used by the migration, overriding an inherited connection setting and restoring it afterward. This prevents migration/setup targeting different databases. It does not print that connection or inspect a private environment file.

The command refuses Production use and refuses pending migrations; startup never automatically changes schemas. This helper targets only the documented default Development database. Custom database/hosting configurations need separately reviewed commands.

Expected success:

```text
Development customer and plant-service eligibility created with audit history.
Development customer setup finished. No passwords or private environment files were inspected.
```

Repeat execution reports that the customer already exists and makes no changes.

## User browser check: restart with customers enabled

After setup succeeds:

```powershell
.\deploy\local\Start-DevelopmentManagement.ps1 -WithCustomers
```

The helper uses the saved `MANAGEMENT_CLIENT_SECRET` process/private `.env` setting, or a hidden prompt if none is configured, followed by the same trusted HTTPS preflight. See [secret setup](MANAGEMENT_SIGN_IN_SETUP.md#4-save-the-client-secret-once-and-start-the-cloud-api). `-WithCustomers` enables both read-only customer lookup and assignment lookup; `-WithAccess` alone still enables only assignment lookup. Neither flag enables order writes. All affected process settings are restored on exit. These helpers support Windows PowerShell 5.1 and PowerShell 7.

Sign in at `https://localhost:7100/management/login`, then open:

```text
https://localhost:7100/management/tenants/11111111-1111-4111-8111-111111111111/plants/22222222-2222-4222-8222-222222222222/customers
```

Expect one `Demo laundry customer`, `truncated: false`, and `orderAccessEnabled: false`. A successful check verifies identity, application membership, customer ownership, and configured plant service together, but not a production order workflow.

## Verification and next step

PostgreSQL tests cover idempotent/concurrent setup, scoped eligibility, inactive/unserved customers, tenant foreign-key enforcement, atomic rollback/recovery, audit protection, and bounded choices. The real OIDC-handler cookie regression also reads the persisted customer after membership authorization and tests wrong scope, anonymous access, removed permission, disabled/Production routes, and storage-failure feedback.

All 295 .NET tests pass, including 11 new customer persistence/eligibility cases. Seven startup-helper cases and three customer-setup cases pass in each supported PowerShell version. These results do not claim a live customer migration or browser check has run.

Startup and customer-setup helper tests use synthetic data and mocked commands, not real credentials or development mutations. The following run without Docker:

```powershell
powershell.exe -NoProfile -File tests/management/Test-DevelopmentManagementHelper.ps1
powershell.exe -NoProfile -File tests/management/Test-DevelopmentCustomerSetup.ps1
```

Run the same scripts with `pwsh` to verify PowerShell 7. The next reviewed step is a session-bound order-access adapter and first protected order endpoint; do not enable order writes just because this preview succeeds.
