# Gateway order-plan download v1

Date: 2026-10-17. Cloud-side implementation only. No gateway downloader/cache, receiving/dispatch workflow, delivery acknowledgement or production route is enabled.

Latest local startup: after the approved migration, the updated management API was started using `Start-DevelopmentManagement.ps1 -WithOrders`. Trusted HTTPS checks returned 200 for `/management` and 404 for this disabled download route. No download permission was granted or gateway delivery performed. The stopped-state description in the migration record below predates this startup.

## Endpoint and access

`GET /api/v1/gateway/order-plans?cursor=...&limit=100` is mapped only in Development with explicit `OrderPlanDownload:Enabled=true`. The boundary requires loopback, HTTPS and `localhost:7100`. It is disabled by default and the existing management starter does not enable it.

Use the existing signed gateway bearer-token validation with a **separate** `laundry_permissions=orders.download` permission. Management cookies and `scans.ingest` alone do not authorize downloads. Exactly one nonzero valid `tenant_id` and `plant_id` claim establishes scope; request parameters cannot choose another scope. No live identity permission grant is included in this implementation.

OpenAPI: `apps/cloud/Laundry.Api/Orders/OpenApi/order-download.v1.yaml`; generated TypeScript types: `packages/api-client/src/generated/order-download-api.ts`. Published fields reference the shared `order-plan.v1` JSON Schema rather than management entities. Runtime returns only stored current filtered payloads, without outbox provenance or management reference fields. Responses use `Cache-Control: no-store`.

## Batches and bookmarks

- `items`: at most `limit` current complete published copies; limit defaults to 100 and must be 1–100.
- `nextCursor`: always present, including empty/caught-up responses. The receiver must eventually save this opaque bookmark atomically with applying its batch locally.
- `hasMore`: true when another current head exists beyond this batch in this request's database snapshot. False means caught up to that snapshot, not permanently finished or known-current while offline.

No cursor means start from the beginning. Unknown/repeated parameters, blank cursors and invalid limits fail with 400. A cursor is protected using ASP.NET Core Data Protection with a dedicated purpose and binds tenant, plant, feed epoch and position. It has no intentional time expiry, permitting long outages; preserve the host key ring/application identity across upgrades. Key loss, tampering, another scope, a mismatched epoch or a position ahead of the feed fails explicitly with `invalid_order_download_cursor` (400). Never silently reset progress or delete local data in response. Recovery/full reconciliation needs a later explicit workflow.

An empty feed returns a protected zero-position/empty-epoch bookmark that can transition to the first real feed incarnation. Normal feed epochs are nonzero UUIDs. An epoch helps detect feed reinitialization, not every possible backup rollback: a database restore and cursor/key-ring recovery require a separately reviewed disaster-recovery/reset procedure; automatic detection of all history rewinds is not claimed.

## Durable ordering and current-copy semantics

`orders.publication_feed_states` holds one nonzero epoch and positive `last_position` per tenant/plant. Each publication change allocates the next position by updating this relational row **inside the owning order/publication transaction**, under the existing transaction-scoped plant writer lock. `publication_heads.change_position` records that head's latest position, with a unique scoped index. A rollback rolls back the counter too; unchanged filtered content does not advance it. PostgreSQL sequences, random IDs, device clocks and recording timestamps are not feed order.

The plant lock remains held until commit. A later writer cannot commit a higher position while an earlier allocation is uncommitted. The download reads feed state and current heads in one repeatable-read snapshot, ordered by position, taking one extra row for `hasMore`. If more exist, the bookmark advances to the last returned position; otherwise it advances to that snapshot's high-water position. Writers after that snapshot receive higher positions and remain downloadable. Position gaps caused by superseded heads are normal.

This is a **current-copy feed**, not an immutable outbox replay. Updating an order moves its current head to a new position. It may appear again on a later page even if an older revision was already returned; the receiver must apply revision/idempotency checks. A request retried after intervening writes can return newer content rather than byte-identical batches. Repeating it without intervening writes returns the same order content. Previously visible notes in superseded immutable outbox records are not re-exported. Already downloaded visible notes can only be cleared after a newer hidden copy reaches the gateway; a response in flight cannot be remotely recalled.

Different payloads under the same scoped revision are conflicts; duplicates are harmless and older revisions must not overwrite newer ones. Those receiver rules are not implemented by this cloud endpoint. No date cutoff, retention purge, tombstone, removal inference or automatic customer-label fan-out is added. Every published head remains eligible for download; customer deactivation/removal policy remains future work. Missing rows, empty batches and errors never instruct deletion. Continuous changes can delay catching up; this is not a frozen multi-request export.

## Migration and activation

Prepared migration `20261017174912_OrderPlanDownloadProgress` adds feed-state storage and head positions. Existing heads get distinct initial positions ordered by order ID within each scope, with a new epoch and matching counter. This is an initial enumeration, not a reconstruction of historical commit order. It preserves existing published payloads/revisions/fingerprints, immutable outbox records, orders and business history; it does not publish missing orders or grant access.

**Applied to the local Compose database on 2026-10-17 with explicit approval.** No API was listening; only the existing cloud PostgreSQL container was started. The helper's build/migration succeeded. Existing records' aggregate full-row digests match before/after, excluding only the newly added head position field. Three existing copies now have positions 1–3 and one nonzero epoch/counter at 3; position constraints/index and append-only outbox triggers were verified. The management API remains stopped; downloading is still configured off and no permission grant, initialization or gateway delivery occurred. Automated suites were not rerun for this operational step.

For another database, stop writers and explicitly apply this migration before starting the new publication-writing build, even if downloading is disabled. Never mix old publication writers (which cannot maintain positions) with the migrated schema. Live route activation and the gateway permission grant require separate approval.

## Errors and boundaries

- 401: missing/invalid/expired gateway bearer token.
- 403: missing download permission, invalid scope or forbidden origin/transport/address.
- 404: feature disabled or non-Development host.
- 400: invalid query or protected bookmark, with a fixed safe code.
- 503: database unavailable, with `order_download_unavailable`; retain local state and retry later.

The GET does not write a gateway checkpoint, acknowledge persistence, mark an outbox record delivered, mutate an order, grant permission or start a worker. It does not make internet/identity availability a dependency of local scanning. Log no payloads, notes or credentials.

## Verification scope

Disposable database tests cover paging, duplicate downloads, empty polling followed by updates, current-only hidden-note clearing, scope isolation, invalid bookmarks, transaction rollback, uncommitted writers, database restart and populated-schema migration preservation. Signed-token HTTP tests cover authentication, dedicated permission, scope validation, query validation, Development/HTTPS/host/address restrictions and storage-outage responses. Full regression results are recorded in `PROJECT_STATUS.md`; live activation and gateway resilience are separate checks.

Final verification (2026-10-17): all **418 full cloud tests**, **29 focused download tests** and **30 shared contract tests** pass. Generated TypeScript typechecking, focused .NET whitespace verification and `git diff --check` also pass. The first focused run exposed a legacy-fixture numbering collision; the first full run passed 413/414 with an older-schema test querying a newly added column. Both fixture issues are corrected; the final full run includes every new test. Sandboxed test/build-host access was unavailable, so final .NET checks used approved normal-host access with disposable databases. Gateway, station, browser and cross-component synchronization suites were not rerun; no live activation/permission change/private credential inspection occurred.

Commands run (repository root unless noted):

```powershell
# Generate source only; does not connect to or migrate the local database.
dotnet ef migrations add OrderPlanDownloadProgress --project apps/cloud/Laundry.Api/Laundry.Api.csproj --context OrderDbContext --configuration OrderDownload -- --environment Development

dotnet test apps/cloud/Laundry.Api.Tests/Laundry.Api.Tests.csproj --no-restore -p:OutputPath=bin/OrderDownloadFinalRegression --logger "trx;LogFileName=download-final-regression.trx" --results-directory artifacts/order-publication-tests
dotnet test apps/cloud/Laundry.Api.Tests/Laundry.Api.Tests.csproj --no-restore -p:OutputPath=bin/OrderDownloadFinalHttp --filter "FullyQualifiedName~Download_|FullyQualifiedName~DownloadEndpoint_" --logger "trx;LogFileName=download-final-http.trx" --results-directory artifacts/order-publication-tests
git diff --check
```

Ran `node --test` inside `packages/contracts`. Inside `packages/api-client`, ran installed `.\node_modules\.bin\openapi-typescript.cmd ../../apps/cloud/Laundry.Api/Orders/OpenApi/order-download.v1.yaml -o src/generated/order-download-api.ts` and `.\node_modules\.bin\tsc.cmd --noEmit`. The standard pnpm generation command initially failed because sandboxed Corepack could not download pinned pnpm; these existing installed tools generated/checked the same sources without installing/updating dependencies. Ran `dotnet format whitespace LaundryOperations.sln --no-restore --include ... --verify-no-changes` for the changed API/security/test files, including the corrected historical-migration fixture.
