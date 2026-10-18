# Gateway-local published order storage

Date: 2026-10-17. Storage and batch-application logic only. No HTTP downloader, worker, station API/UI, order selection or receiving/dispatch action is introduced.

Follow-up: a separate [explicit one-time Development downloader](GATEWAY_ORDER_DOWNLOAD_SETUP.md) now composes these storage primitives without starting the gateway host or workers. Approved live verification saved three order copies; an empty caught-up rerun retained them and advanced local progress to revision 2 with two receipts in total. Existing plant/cloud data was verified unchanged. The storage-only implementation and empty-table migration history below predate that live download; no automatic worker is enabled.

## Scope and ownership

An optional Development [local summary read interface](LOCAL_ORDER_LIST_API.md) now reads these current copies without registering the downloader/store in normal startup. It is separately permission-protected, excludes notes and defaults off; no live activation or UI connection occurred.

The gateway uses its existing separate plant PostgreSQL database and `PlantDbContext`, following ADR 0005. Its `Orders` module owns independent persistence models; no cloud entities or miscellaneous shared business-logic package is referenced. It embeds the existing canonical `order-plan.v1` JSON Schema using the already installed JsonSchema.Net dependency.

`OrderDownloadStore` receives a nonempty `OrderDownloadScope` from trusted gateway configuration at composition time. Apply/read calls cannot select another scope. A future downloader must authenticate the cloud HTTPS response and bind that configuration to its gateway registration; this storage primitive does not authenticate a caller or prove a payload came from the cloud. It is deliberately not registered in the running host or exposed as an endpoint yet.

## Tables

| Table in `plant` | Contents |
|---|---|
| `order_plans` | Scoped order ID, published revision, canonical filtered JSON, SHA-256 fingerprint and local storage UTC time; one current working copy per order |
| `order_download_progress` | Scoped opaque bookmark, positive local progress revision and last successfully committed check UTC time |
| `order_download_receipts` | Globally unique incoming batch ID, scope, request fingerprint, resulting local progress revision, applied/duplicate/older counts and UTC recording time |

The receipt is transactional inbox metadata for safe retry after a lost response, not another order history. It retains neither order payloads nor old visible/hidden note text, and does not persist an old bookmark. Its fingerprint is not encryption or anonymization. EF sync/async guards and database update/delete/truncate triggers prevent ordinary receipt mutation; administrator privileges are not a technical immutability guarantee. Receipts reference scoped progress and have a unique scoped progress-revision index. No retention/cleanup policy is added.

## Apply contract and atomicity

`ReadProgressAsync` returns revision 0/null bookmark/null check time until the first batch commits. Read failures throw; they must not be interpreted as empty initial state.

`ApplyAsync(batchId, expectedProgressRevision, requestedCursor, responseJson)` requires:

- A nonzero globally unique batch ID allocated by the future receiver. Keep the same ID, expected revision, request bookmark and exact response for an uncertain-result retry. JSON property order/escaping/numeric representation can differ if canonical order contents and batch item order are unchanged.
- The local progress revision and exact opaque bookmark used to start that download. Local revision is distinct from each order's cloud-owned published revision and the cloud's hidden feed position.
- A bounded complete response (`items`, `nextCursor`, `hasMore`), at most 100 unique orders/4 MiB JSON and a nonblank bookmark of at most 2048 characters. An empty batch cannot claim `hasMore=true`.

Parse and validate the complete batch before applying it. Reject duplicate JSON properties, unknown/missing fields, unsupported schema/status, invalid IDs/date ordering/limits, repeated order IDs, hidden note text and tenant/plant mismatch. The shared JSON Schema is authoritative for individual fields; additional gateway checks enforce intended scope and cross-field dates. A malformed item cannot be skipped while its bookmark advances.

Inside one database transaction, acquire an incoming-batch advisory lock, then a separate scope-specific order-download lock. These do not reuse scan/outbox locks. Check an existing batch receipt first: an exact accepted retry returns that historical receipt without changing copies, bookmark or last-check time, even after newer batches committed. Reusing the ID for different content/scope is `order_batch_id_conflict`.

For a new batch, both expected local progress revision and requested bookmark must match the current row. Otherwise fail with `stale_order_download_progress`; a delayed/concurrent response cannot move current progress backward. The future receiver must reread current progress after an uncertain commit/historical receipt rather than treating the receipt as current state. No persisted download-request lease or network lifecycle is claimed by this storage seam.

Apply all rows using these rules:

| Incoming published revision | Action |
|---|---|
| No local copy, or newer than local | Insert/replace the complete working copy |
| Same revision and canonical content | Count as a harmless duplicate; do not replace its storage timestamp |
| Older than local | Keep the newer local copy and count as older |
| Same revision, different content | Reject the entire batch with `order_publication_revision_conflict` |

Typed allowlisted canonicalization normalizes property layout, UUID casing, escaped strings and equivalent integral JSON numbers, not business text. A changed note/customer label under the same revision remains a conflict. A newer hidden copy stores explicit null and overwrites prior working-copy text; delayed older visible copies cannot restore it. This is versioned reference-data replacement, never generic last-write-wins physical inventory handling.

Finally, save all changed copies, the next opaque bookmark, incremented local progress revision, check time and immutable receipt **in that same transaction**. Return success only after commit. A save/commit failure leaves no partial batch and may leave acknowledgement uncertain; retry unchanged. Progress revision overflow fails before writing. Empty successful polls update progress/check time without deleting copies. Older/duplicate items can advance a genuinely current batch's bookmark because their effects are already represented locally.

## Offline, privacy and failure boundaries

No cloud/identity/network call is made while applying or reading this store. Existing copies survive database/process restart and remain available to a future local read interface during WAN outages. A missing response, failed request, empty page or missing row never implies deletion. Last successful check is a local UTC observation, not proof of current cloud state; do not use it for ordering. Exact receipt retries do not renew freshness.

Hidden notes clear the current working row; this is not secure erasure of PostgreSQL pages, backups or already displayed/in-flight copies. No historical payload is copied into the receipt. Exceptions expose fixed safe codes, never payloads, notes or bookmarks. Future diagnostics should correlate the known batch ID without dumping response contents. Invalid schema/conflicting revisions require attention rather than endless silent retry or a skipped bookmark; user-facing diagnostics/recovery remain later work.

No automatic order expiry, customer deactivation/removal, gateway re-registration, cloud-authority switch, key loss or restored-history reconciliation is designed here. No new order, operator permission, business movement, charge, dispatch or cloud delivery receipt is generated by saving these working copies.

## Migration and activation

Migration `20261017183956_LocalOrderDownloadStorage` creates only the three new tables, constraints/indexes and receipt immutability function/triggers. It does not backfill, modify scan/security/outbox evidence or import local orders. **Applied to the local plant database on 2026-10-17**, with the gateway stopped. All eight existing plant tables retained identical counts and full-row aggregate digests; all three new tables are empty. Keys, constraints, indexes and enabled receipt-protection triggers/function were verified. Only the existing plant PostgreSQL container was started; no gateway startup, download or private credential inspection occurred. Another database requires its own explicit migration step. Live initialization or starting a downloader remains a separately approved step. Existing runtime scan code does not call this unregistered store.

## Verification

Tests use disposable PostgreSQL containers, synthetic published examples and local fixed clocks. They cover atomic commits, save rollback after SQL writes, lost commit acknowledgement, old receipts after newer progress, duplicate/concurrent batches, stale responses, newer hidden/older visible copies, equal-revision conflicts, cross-scope/mixed batches, schema/privacy/size checks, empty polling, representation normalization, database restart/outage, progress overflow, append-only SQL/EF guards, migration preservation and model consistency. Results and exact commands are recorded in `PROJECT_STATUS.md`; this is not a live gateway download.

Implementation results: **155 gateway tests** pass, including **22 new storage cases**; the existing cross-component synchronization test and all **30 shared contract tests** also pass. The first focused run passed 21 then-present cases; final regression includes the added bounds/overflow check and expanded representation check. Focused formatting verification and diff checks pass. Unchanged full cloud, station/client and browser suites were not rerun. The subsequent approved local migration passed its build and SQL integrity/schema checks; automated suites were not rerun for that operational-only step. No live download occurred.

Local migration command (connection loaded internally from public Development configuration, with the loopback target verified; never print the connection string):

```powershell
dotnet ef database update 20261017183956_LocalOrderDownloadStorage --project apps/edge/Laundry.Edge/Laundry.Edge.csproj --context PlantDbContext --connection $settings.ConnectionStrings.Plant -- --environment Development
```

Here `$settings` is the parsed public `apps/edge/Laundry.Edge/appsettings.Development.json`. This is the command recorded for the completed guarded operation, not an instruction to inspect private environment files or migrate another target blindly.

Commands run from the repository root:

```powershell
# Generate migration source only; no local schema mutation.
dotnet ef migrations add LocalOrderDownloadStorage --project apps/edge/Laundry.Edge/Laundry.Edge.csproj --configuration LocalOrders -- --environment Development
dotnet test apps/edge/Laundry.Edge.Tests/Laundry.Edge.Tests.csproj --no-restore -p:OutputPath=bin/LocalOrderStorageFocused --filter FullyQualifiedName~OrderStorage_
dotnet test apps/edge/Laundry.Edge.Tests/Laundry.Edge.Tests.csproj --no-restore -p:OutputPath=bin/LocalOrderStorageRegression --logger "trx;LogFileName=local-order-storage.trx" --results-directory artifacts/order-publication-tests
dotnet test tests/synchronization/Laundry.Synchronization.Tests/Laundry.Synchronization.Tests.csproj --no-restore -p:OutputPath=bin/LocalOrderStorageSync --logger "trx;LogFileName=local-order-storage-sync.trx" --results-directory artifacts/order-publication-tests
git diff --check
```

Also ran `node --test` in `packages/contracts`, and `dotnet format whitespace LaundryOperations.sln --no-restore --include ... --verify-no-changes` for the new order module, `PlantDbContext` and storage tests. Isolated build outputs preserve the running management executable; database tests use disposable containers rather than the named local plant database.
