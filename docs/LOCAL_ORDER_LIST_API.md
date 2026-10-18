# Read-only gateway order-list API

Date: 2026-10-17; live UI follow-up 2026-10-18. Development implementation only. A [read-only station UI](STATION_LOCAL_ORDERS_UI.md) consumes this contract and has been verified using one explicitly approved dedicated in-app-browser source. That test gateway is left running with forwarding off and a retained audited read grant; normal startup defaults/other browser permissions are unchanged. Order selection, receiving/dispatch and production human authorization remain unimplemented.

## Scope and activation boundary

`GET /api/order-plans` lists summaries from already downloaded plant PostgreSQL copies. It makes no cloud/identity network request and never starts a download, changes an order or grants permissions. Normal scan acceptance remains independent.

The route is mapped only with all of:

- Development environment;
- the existing `ScanAcceptance:Enabled` gateway registration configuration;
- explicit `LocalOrderList:Enabled=true` (committed Development default is **false**).

Requests require loopback, trusted `https://localhost:7200`, an active enrolled browser source cookie and its **separate `orders.read` source permission**. The gateway derives tenant/plant from `SourceIdentity`; callers cannot supply another scope. `scans.submit` alone is insufficient. Expired/revoked credentials, disabled sources and wrong scope fail closed; adapters cannot use this browser-only endpoint. Present Origin/Fetch Metadata headers must identify this same origin or an ordinary direct navigation. No CORS or unauthenticated fallback is added. GET is read-only and requires no mutating-request antiforgery token.

**No live station permission was granted and no gateway service was started/restarted for this implementation.** Existing enrollment continues to grant scans only. Local activation plus an explicitly authorized, audited test-station permission grant should be a separately reviewed next step, not a reason to silently broaden all existing sources. This is source authorization for a Development prototype, not operator authentication. An authorized human/operator session remains necessary before production use.

## Request and response

A separately reviewed [temporary-station access verification helper](LOCAL_ORDER_ACCESS_VERIFICATION.md) is implemented/tested and live-verified. It granted/removed `orders.read` only for its own new source, read all three local summaries, retained audits, revoked that source/credential and stopped its temporary host. All pre-existing plant/cloud records were verified unchanged. It does not grant normal browser access and leaves ordinary startup disabled; earlier no-live-change descriptions record the implementation step.

Optional query parameters:

- `limit`: 1–50, default 50;
- `cursor`: opaque local list continuation, at most 2048 characters.

Unknown/repeated parameters, invalid limits and blank/invalid cursors are rejected. No customer/date/search filter is implemented yet. Stable PostgreSQL order-UUID keyset ordering is a technical paging order, not chronological receiving/delivery order.

The response contains only:

- `items`: order ID/number, customer ID/code/display name, planned receiving/delivery dates, currently supported `planned` status and published version;
- `nextCursor`: continuation when more items exist, otherwise null;
- `hasMore`: whether this local snapshot contains another item;
- `lastSuccessfulPageAtUtc`: UTC time of the latest committed download page, or null before any download.

**No notes (visible or hidden), customer reference, raw payload, fingerprints, download bookmark, receipt history, source/station/credential IDs or tenant/plant identifiers are returned.** An allowlisted DTO is built after validating each returned stored copy's shared schema, scope, ID/version and content fingerprint. An invalid stored copy fails the whole response rather than being skipped or returned raw.

The timestamp describes download activity, including empty polls and partial multi-page runs. It does not prove complete catch-up, cloud connectivity or present freshness. Before the first download, an empty list and null timestamp mean no downloaded working data—not that the cloud has no orders. After a download, empty lists still never imply deletion or order completion. No arbitrary stale-age cutoff or online/current flag is invented.

All endpoint responses use `Cache-Control: no-store` and `Vary: Cookie`. This does not implement browser emergency storage, offline PWA caching or secure erasure of previously displayed data.

## Coherent paging and failure behavior

Each request reads scoped progress and order rows in one repeatable-read, read-only database transaction. It takes at most `limit + 1` rows and returns at most `limit`. The continuation is protected with ASP.NET Core Data Protection under a dedicated local-list purpose and binds scope, current local download-progress revision and final returned order ID. It is **not** the cloud download bookmark.

If a download advances progress between requests—even an empty caught-up poll—the old continuation returns 409 `local_order_list_changed`. Start the list again without that continuation; do not reset cloud download progress. This avoids silently mixing pages from different local download revisions. A page already returned cannot be recalled. Key loss, tampering and cross-scope continuation reuse return 400; starting a fresh list is safe and does not delete data. No fixed expiry or historical multi-request snapshot is promised.

Errors return fixed JSON `code` values:

| Status | Meaning |
|---|---|
| 400 | Invalid query or protected local list cursor |
| 401 | Missing/invalid/expired/revoked/wrong-scope browser source |
| 403 | Source lacks `orders.read`, is not a browser station, or transport/origin is forbidden |
| 404 | Route not enabled or host not Development |
| 409 | Download progress changed; restart listing |
| 503 | Local source/order storage unavailable or a stored order copy failed validation |

503 must not be rendered as an empty order list. Storage-outage diagnostics log a correlation identifier without exception details, cookies, order text, query strings or cursors. Bounded ordinary request retries after recovery are safe; GET performs no operational mutation. No new retention, reconciliation, business state or production access policy is introduced.

## Contract and verification

OpenAPI: `apps/edge/Laundry.Edge/OpenApi/station-api.v1.yaml`. The regenerated station client is `packages/api-client/src/generated/station-api.ts`; the existing `createGatewayClient` exposes the typed operation. Exported `LocalOrderSummary`/`LocalOrderPage` types come from that generated schema rather than handwritten TypeScript duplicates. The PWA now consumes the operation; see [UI implementation and live verification](STATION_LOCAL_ORDERS_UI.md).

Disposable PostgreSQL/synthetic-station tests cover scoped read-only paging, deterministic continuation, 50-row bounds, partial-download metadata, empty initial state, note/internal-field omission, dedicated permission and immediate local permission removal, source expiry/revocation/disablement/wrong scope, adapter rejection, query validation, cursor tampering/scope/revision changes, transport/origin boundaries, explicit Development activation, stored-copy corruption and database outage/restart recovery. Production data/credentials and the named local database are not used by these tests.

Verification commands (repository root unless noted):

```powershell
dotnet test apps/edge/Laundry.Edge.Tests/Laundry.Edge.Tests.csproj --no-restore -p:OutputPath=bin/LocalOrderListRegression --logger "trx;LogFileName=local-order-list.trx" --results-directory artifacts/order-publication-tests
dotnet test tests/synchronization/Laundry.Synchronization.Tests/Laundry.Synchronization.Tests.csproj --no-restore -p:OutputPath=bin/LocalOrderListSync --logger "trx;LogFileName=local-order-list-sync.trx" --results-directory artifacts/order-publication-tests
git diff --check
```

Inside `packages/api-client`, ran installed `openapi-typescript` against the station schema and `tsc --noEmit`. Inside `apps/station-pwa`, ran installed `tsc -b --pretty false`; no package/dependency update was needed. Focused .NET whitespace verification also covers changed endpoint/query/security/host/test files. Final results and checks not rerun are recorded in `PROJECT_STATUS.md`.

Final results: **230 gateway tests** pass, including **34 local-list cases**, plus the existing cross-component synchronization test. Both TypeScript checks, client generation, focused formatting verification and diff checks pass. Initial focused fixtures were corrected for upstream host filtering and Docker's reassigned disposable-container port after restart. Full regression was rerun after adding empty/null corrupted-copy cases. Unchanged full cloud/contract suites, station UI tests and browser workflows were not rerun. No live route activation, source grant, service restart or database mutation was performed.
