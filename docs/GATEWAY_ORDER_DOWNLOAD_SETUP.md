# One-time Development gateway order download

Date: 2026-10-17. Explicit local command only; no automatic polling, HTTP endpoint, operator UI, receiving/dispatch action or production activation.

## What is implemented

`Download-DevelopmentOrders.ps1` runs the existing gateway executable with `--download-orders-once` in a separate `OrderDownloadOnce` build configuration. That branch exits before building/starting the web host, registering scan forwarding or starting hosted workers. Normal gateway startup remains unchanged. No new dependency, protocol, datastore, migration or permission is introduced.

The command is restricted to Development and the existing localhost plant database, demo tenant/plant registration, gateway client and Keycloak token endpoint. The cloud endpoint is fixed to `https://localhost:7100/api/v1/gateway/order-plans`; request parameters cannot select another tenant/plant. The cloud independently validates the signed gateway token and `orders.download`; the receiver also validates every response item against its configured scope and shared published-order schema. This tool is deliberately not a generic production installation command.

The helper consumes the existing saved gateway credential internally through normal Compose configuration resolution, never prints it and restores inherited process environment settings on success/failure. Agents must not inspect private environment files or resolved Compose configuration. Nothing is passed as a command-line secret or written to a temporary payload/credential file. HTTPS certificate/hostname checks stay enabled; redirects, proxies and cookies are disabled on both HTTP clients. Do not bypass certificate validation to make the command work.

## Run after prerequisites are ready

The helper is implemented/tested and **verified against local development orders on 2026-10-17**. It saved three published order copies; a second invocation safely resumed and committed an empty caught-up poll without changing those copies. See [local live verification](#local-live-verification). Automatic downloading remains off.

Prerequisites: existing cloud/plant databases and Keycloak running; explicit migrations already applied; the separately approved development gateway `orders.download` grant present; management API running with `Start-DevelopmentManagement.ps1 -WithOrders -WithOrderDownloads`. This helper does not start/restart services, migrate databases, initialize orders or grant access. Pending gateway migrations stop the command before HTTP downloading.

From the repository root:

```powershell
.\deploy\local\Download-DevelopmentOrders.ps1
```

It reads current local progress before each request, downloads up to 100 copies per page and applies a complete validated page using the [existing transactional store](GATEWAY_ORDER_STORAGE.md). Current copies, opaque bookmark, local revision, check time and immutable inbox receipt commit together. Successful earlier pages remain saved if a later page fails. The request is outside the database transaction and does not hold scan/outbox locks while waiting on the cloud.

The invocation is bounded to 100 pages and five minutes; each HTTP download attempt has a ten-second deadline, while existing identity-token acquisition retains its own bounds. Ctrl+C requests cancellation. The output contains a run correlation ID, status, acknowledged page/copy counts and a fixed safe error code—not order text, credentials, bearer tokens, bookmarks or HTTP error bodies.

| Status / process exit | Meaning |
|---|---|
| `caught_up` / 0 | The final committed page reported no more copies in its cloud snapshot; not a promise of permanent freshness |
| `more_available` / 2 | Page bound reached after successful commits; run again to continue |
| `stopped` / 1 | A failure/cancellation occurred; saved copies/progress remain intact |

The PowerShell wrapper explains exit 2 without throwing; it throws a safe message for exit 1. Summary counts cover only acknowledged pages in this invocation, not all orders at the plant. An uncertain commit followed by cancellation or another failure can have saved work not reflected in those counts; inspect durable progress through a future diagnostic/read interface, not by guessing from the summary.

## Retry, outage and conflict behavior

- Rerunning starts from durable progress. It does not reset the bookmark or delete local copies. A process restart after commit but before console output continues from the committed page.
- One 401 invalidates the cached token and permits one refresh/retry of the same GET. A second 401, 403 or other error stops without applying its body. HTTP/identity outages and timeouts do not block independent local scan acceptance.
- If applying a downloaded page encounters a storage/commit acknowledgement failure, retry that exact in-memory batch once, preserving batch ID, request progress and response. Do not refetch different content under the same batch ID. If the process dies, the next invocation reads durable progress instead; no persistent pending-request lease or exactly-once console output is claimed.
- Reread progress after acknowledgement; a historical inbox receipt is not current state. Overlapping invocations cannot rewind progress: stale responses or intervening progress changes stop explicitly. No last-write-wins inventory rule is introduced.
- Validate the full bounded UTF-8 JSON response before writing, including scope, dates, hidden-note privacy, duplicates, unknown fields and page limits. A malformed/conflicting item rejects the whole page. A `hasMore` page must contain items and change its bookmark. The page cap also bounds changing/repeating feeds.
- Newer copies replace older current reference data, equal copies are harmless, delayed older copies cannot restore cleared notes, and equal revision/different content is an explicit conflict. Empty successful polls retain all copies.
- An invalid/expired-by-key-loss bookmark, permission/configuration failure or revision conflict needs investigation. Never silently reset progress, skip a malformed page, print private error bodies or broaden permissions. More detailed operator diagnostics and reconciliation remain later work.

## Verification

Tests use synthetic HTTP/token responses and disposable PostgreSQL containers, not the named local development orders. Coverage includes restart/resume, bounded runs, partial-page-run failure, empty polling, duplicates/older revisions, hidden-note clearing, equal-revision conflict rollback, overlapping responses, lost commit acknowledgement, repeated save rollback, identity/database/network outages, timeouts/cancellation, HTTP rejection without error-body inspection, UTF-8/schema/scope/size/media validation and Development installation guards. Synthetic PowerShell tests cover normal/bounded/failed runs, missing credentials/configuration failures and restoration of both inherited and absent environment variables.

Commands (repository root):

```powershell
dotnet test apps/edge/Laundry.Edge.Tests/Laundry.Edge.Tests.csproj --no-restore -p:OutputPath=bin/OrderDownloadOnceFinal --logger "trx;LogFileName=download-once-final.trx" --results-directory artifacts/order-publication-tests
dotnet test tests/synchronization/Laundry.Synchronization.Tests/Laundry.Synchronization.Tests.csproj --no-restore -p:OutputPath=bin/OrderDownloadOnceSync --logger "trx;LogFileName=download-once-sync.trx" --results-directory artifacts/order-publication-tests
.\tests\management\Test-DevelopmentOrderDownloadHelper.ps1
git diff --check
```

Final results: **196 full gateway tests** pass (41 new downloader/command cases), the existing cross-component synchronization test passes, and all **six synthetic PowerShell helper cases** pass. Focused formatting verification, PowerShell parsing, native CLI Production-rejection smoke check and diff checks pass. Initial verification caught a wrapped EF connection failure and a reused JSON test node; both were corrected and the complete gateway suite rerun successfully. Sandboxed formatting could not access its build-host pipe; approved normal-host verification succeeded. Unchanged full cloud, contract/client, station and browser suites were not rerun. No live import, service restart, schema/permission change or UI/browser change is part of this implementation slice. See `PROJECT_STATUS.md` for the complete handoff.

## Local live verification

After separate approval, ran `Download-DevelopmentOrders.ps1` twice on 2026-10-17 against the already-running local services. First run saved three current copies, one scoped progress row at local revision 1 and one immutable inbox receipt. A server-side normalized full-JSON aggregate digest/count matched the current cloud publication heads without displaying payload text.

Second run resumed the saved bookmark, received an empty caught-up response, preserved all three copy rows including storage timestamps, and atomically advanced progress to revision 2 with another receipt. Receipt applied counts were 3 then 0; duplicate/older counts were zero. Empty caught-up polling is expected—not three duplicated orders. Progress/receipt timestamp/revision consistency, scope/order/revision alignment, hidden-note privacy and private-reference exclusion checks found zero violations.

Before/after full-row aggregate counts/digests matched for every one of the 20 cloud tables and all eight pre-existing plant tables (including 17 scans and 17 outbox records). No cloud business/access/publication data or source registration changed. Credentials were consumed internally by normal helper execution only; no private environment-file inspection, payload/credential/bookmark output, service restart, schema mutation, permission grant, scan forwarding, gateway web-host startup or automatic worker activation occurred. Automated suites were not rerun for this operational-only step; both helper runs, SQL integrity/content/privacy checks and diff verification passed.
