# Protected planned-order creation: Development proof

Date: 2026-10-11.
Status: first create endpoint implemented and tested. On 2026-10-11 the user applied the live Development migration, created `ORD-000001` (`planned`, version 1, HTTP 201), and retried the same retained command with the same ID/number/timestamps. Read-only scoped database counts confirm one demo order, one creation audit, and one command receipt. The migration itself created no order; the deliberate browser command did.

## Enabled actions

The user has verified both list and detail JSON responses. For everyday browsing, use the new single [management navigation entry point](MANAGEMENT_NAVIGATION_SETUP.md) instead of keeping individual API URLs bookmarked. It provides readable existing-order views; creating/editing orders in the UI remains deferred.

An authorized signed-in person can deliberately create a planned order for an eligible customer. The system supplies immutable order ID/number, tenant/plant attribution, authenticated creator, UTC timestamps, and version 1. Creation does not activate receiving, attach items, dispatch anything, or charge a customer.

`POST /api/v1/plants/{plantId}/orders` creates an order. `GET /api/v1/plants/{plantId}/orders/{orderId}` reads its current saved plan. `GET /api/v1/plants/{plantId}/orders` now lists saved plans. History, editing, activation, and completion endpoints remain unimplemented.

## List saved orders (current small live check)

The user verified the protected detail read of `ORD-000001`, matching its original ID, dates, status, version, and timestamps. Listing requires no migration or seed change. Stop the API with **Ctrl+C**, then run `.\deploy\local\Start-DevelopmentManagement.ps1 -WithOrders` from the repository root. Sign in if needed and open:

```text
https://localhost:7100/api/v1/plants/22222222-2222-4222-8222-222222222222/orders
```

Expected shape: `{ "items": [...], "nextCursor": null }`, including the demo order. This reads existing plans, never creates/activates orders, and excludes internal creator/editor identities. Current plant-scoped `orders.read` is required, not a gateway token, CSRF token, or idempotency key. Deactivated customers' orders remain visible.

This first list has no filters. `limit` defaults to 25 and accepts 1–100. Orders sort ascending by planned receiving date then ID. If `nextCursor` is not null, pass it URL-encoded as `cursor`, keeping the same limit. Protected tokens bind tenant, plant, limit, and unfiltered query version. Changed scope/limit, malformed tokens, duplicate parameters, and unknown filters return 400 `invalid_order_query`. Tokens are not access grants: current membership/read permission is checked on every page. Data-protection key loss can invalidate a token; restart from page one. Paging does not freeze records against concurrent edits/inserts. Empty pages have empty items and null cursor. Authorization, ambiguity, transport, and outage behavior match details. Live listing verification remains pending restart.

## View the saved order (verified by the user)

No new migration, seed, or order creation is needed. Stop the API terminal with **Ctrl+C**, then restart from the repository root:

```powershell
.\deploy\local\Start-DevelopmentManagement.ps1 -WithOrders
```

Sign in through `https://localhost:7100/management/login` if needed. Open the saved creation response's `Location` under `https://localhost:7100`. It should return HTTP 200 and the saved order JSON, not a management UI yet. The already created demo order should retain its ID, `ORD-000001`, dates, and version 1.

Reads require current `orders.read` permission for the resolved tenant/plant, but no CSRF token or idempotency key. They expose the same public fields as creation and the current ETag, never internal creator/editor identities. Deactivated customers do not hide existing order history. Unknown and out-of-scope orders return `order_not_found` (404) after authorization; unauthorized plants return 403, ambiguous tenant scope 409, and storage outages a safe 503. Every response is `no-store`; conditional GET is not implemented. Reading never changes the order, audit, receipt, or number counter. The user verified this detail response; listing is the pending live check.

Management-cookie identity remains separate from gateway/source credentials. The caller cannot supply tenant, actor, station, status, order number, or audit fields. Unknown and duplicate JSON properties are rejected. Tenant is derived from exactly one current active assignment for the URL's plant; no assignment denies access, and multiple tenant assignments for the same plant return `plant_scope_ambiguous` instead of guessing. A production tenant/plant catalog remains a separate prerequisite.

## HTTP contract

The OpenAPI contract is `apps/cloud/Laundry.Api/Orders/OpenApi/orders.v1.yaml`. Generated TypeScript types and `createOrderPlanningClient` are in `packages/api-client`; station/gateway clients are not reused for management. Generate clients with `corepack pnpm generate:api-client` and type-check the package before a later UI consumes them.

- Require the protected management session, same-session `X-Management-CSRF` token, one nonzero dashed UUID `Idempotency-Key`, and `application/json`.
- Require `customerId`, `plannedReceivingDate`, and `plannedDeliveryDate`. Both dates use exact `YYYY-MM-DD`, and delivery cannot precede receiving; same-day and advance planning are allowed.
- Optional `customerReference`/`notes` accept text or null, with the previously reviewed 100/2000-character limits. The request is bounded to 16 KiB of encoded JSON, including chunked bodies.
- Success is `201`, with the durable order representation, ETag, and Location. An unchanged authorized retry returns the original status/body/headers without duplicating the order, audit, or number allocation.
- A changed normalized request using the same actor-scoped key returns 409. Retry the same request under the same human identity; do not transfer a pending command to another account.
- Current permission is checked on retries too. A recorded historical receipt remains an acknowledgement, even if the customer later becomes ineligible; replay never reapplies an old business mutation.
- Invalid fields/key/CSRF/customer return 400; unauthenticated or machine-only requests return 401; unauthorized scope/permission return 403; ambiguous scope or changed key reuse return 409; oversized/non-JSON requests return 413/415.
- Storage failure returns 503 with `order_storage_unavailable` and a safe correlation identifier. A response loss can follow commit: never infer that a second order should be created. Retry the same key/body.
- This opt-in proof uses only exact loopback HTTPS `https://localhost:7100`. Disabled or non-Development routes return 404. No remote production/LAN rollout or unauthenticated fallback is implied.

Error bodies use Problem Details with stable codes, not request contents, credentials, notes, or raw exceptions. Framework cookie challenges and transport rejections may have empty bodies; the future UI must handle status codes as well as structured problems.

## Coordinated durable authorization

The request-bound adapter binds the stable verified human to `IOrderPlanningAccess`; a forged scope with another issuer/subject is denied. Once the order transaction begins, the membership, permission, customer, and plant-service participants join the same database connection/transaction rather than checking unrelated snapshots.

Owning modules hold PostgreSQL `FOR SHARE` locks on the relevant membership, permission row, customer, and exact plant service until the order transaction commits/rolls back. Deactivation or deletion cannot slip between a successful check and the durable save. Membership/permission are locked before customer/service; future administrative mutations must use compatible lock ordering. Existing per-plant order write serialization and idempotent receipts remain intact.

The order, counter, audit, and command receipt are committed together, and success is not returned earlier. Participants keep their own persistence mappings; the composition seam shares `DbConnection`/`DbTransaction`, not persistence entities or a distributed transaction. This uses the existing modular-monolith database, not a new dependency or service. References: [EF cross-context transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions#cross-context-transaction), [PostgreSQL row locking](https://www.postgresql.org/docs/current/explicit-locking.html).

This does not implement generic permission/customer editing or production administrative APIs. Tests simulate privileged record changes and prove that order writes respect current state/locks. Production inventory/workflow conflicts remain distinct from order planning and are not resolved through last-write-wins.

## User setup: apply the existing order migration explicitly

Complete the previously verified management assignment and customer/service setup first. Stop the running API with **Ctrl+C**, then run from the repository root:

```powershell
.\deploy\local\Initialize-DevelopmentOrderStorage.ps1
```

This starts the existing cloud database, explicitly applies only `OrderDbContext` migrations using the committed disposable Development connection, and creates no order. It does not inspect private environment files, reset existing data, alter identity accounts, or automatically migrate at normal startup. Custom database/deployment settings require separately reviewed commands.

Expected final message:

```text
Development order storage migration finished. No order was created and no existing data was reset.
```

Restart after migration succeeds:

```powershell
.\deploy\local\Start-DevelopmentManagement.ps1 -WithOrders
```

The helper still loads the saved Development client secret and validates it without printing it. `-WithOrders` also enables the prerequisite access/customer read routes. `-WithCustomers` alone continues to leave order creation disabled. All affected process settings are restored when the helper exits.

The `orderAccessEnabled` status field now reflects whether this create feature is configured, not whether a particular person/customer/request is authorized. Each create request still checks all permissions and eligibility. A read-only/unassigned account does not gain write access because the feature flag is true.

## User browser creation and safe retry

After startup, sign in at `https://localhost:7100/management/login`. Run the following only from the signed-in `/management/session` page, never Chrome's internal error page. It intentionally creates one synthetic planned order. The retained key/body make re-running it a retry, not another creation:

```javascript
const saved = sessionStorage.getItem('laundry-demo-planned-order');
const command = saved ? JSON.parse(saved) : {
  key: crypto.randomUUID(),
  body: {
    customerId: '33333333-3333-4333-8333-333333333333',
    plannedReceivingDate: '2026-10-12',
    plannedDeliveryDate: '2026-10-14',
    customerReference: 'DEMO-REQUEST-01',
    notes: 'Synthetic development demonstration order.'
  }
};
sessionStorage.setItem('laundry-demo-planned-order', JSON.stringify(command));
const csrf = await fetch('/management/antiforgery').then(r => r.json());
const response = await fetch('/api/v1/plants/22222222-2222-4222-8222-222222222222/orders', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'Idempotency-Key': command.key,
    [csrf.headerName]: csrf.requestToken
  },
  body: JSON.stringify(command.body)
});
({ status: response.status, order: await response.json() });
```

This session-storage entry contains only a synthetic command/key, never a login token or client secret. Keep it unchanged after a timeout/503 and retry as the same `management-demo` account. Successful output should show status 201, a generated order number/ID, status `planned`, the requested dates/customer, and version 1. Re-running with the retained key must show the same order, not a second one. The fixed sample dates are illustrative planned calendar dates; creation timestamps come from the actual server clock.

To intentionally demonstrate another order only after the previous outcome is confirmed, remove the demo entry with `sessionStorage.removeItem('laundry-demo-planned-order')`, then run the creation flow again with a new key. Do not clear it merely because an acknowledgement was uncertain. A real management UI must manage pending commands and actor changes more comprehensively; this console proof is not that UI.

## Verification and next boundary

Tests cover durable creation with trusted actor audit, concurrent/uncertain-acknowledgement retries, changed payload conflicts, permission removal, eligibility deactivation, historical receipts, invalid/duplicate/unknown fields, dates/limits, CSRF, valid gateway rejection, transport/environment gates, ambiguous tenant scope, transactional rollback, and actual row-lock conflicts until commit. The real OpenID Connect cookie regression also creates an order after customer/membership lookup.

Latest list verification: `dotnet test LaundryOperations.sln --no-restore -p:OutputPath=bin/OrderListVerification/` passes all 337 tests (203 cloud, 133 gateway, one cross-component). New list coverage includes empty/default/max pages, tied-date stable ordering, continuation without duplicates, tampered/invalid/wrong-scope/changed-limit cursors, tenant/plant isolation, and real OIDC-session listing. Existing read-access, revocation, deactivated-customer, machine rejection, ambiguity, transport, and outage tests also exercise listing. Order OpenAPI regeneration, `corepack pnpm test:station` (25 tests and client/station type checks), `corepack pnpm test:contracts` (20 tests), focused `dotnet format --verify-no-changes`, and `git diff --check` pass. Helper cases were not rerun because no startup helper changed. Databases/output folders are isolated; no real order or private environment file was inspected/changed. Live details are verified; live listing remains pending.

Migration/startup helpers are tested with synthetic mocked commands on Windows PowerShell 5.1 and PowerShell 7. The user has applied the live migration and verified creation/retry/details; the agent has not created a live order. The next live check is opening the plant order list after restarting. Order list/detail/edit UI, receiving, dispatch, scheduling, completion, billing, offline operator authorization, and cloud-to-gateway order publication remain unimplemented.
