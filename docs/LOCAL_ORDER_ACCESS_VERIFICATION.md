# Temporary-station local order-access verification

Date: 2026-10-17. Implemented, tested in isolation and **live-verified against the local plant database**. Temporary access was removed, the test source/credential revoked and the temporary gateway stopped. Normal startup defaults and existing browser permissions remain unchanged.

## Purpose and boundary

`Test-DevelopmentLocalOrderAccess.ps1` invokes the existing gateway with `--verify-local-order-access` in an isolated `LocalOrderAccessTest` build configuration. The command hosts its own temporary gateway in the same process, forces scan forwarding off and stops/disposes that host afterward. It never locates/kills/adopts an existing gateway process. An occupied port 7200 stops the wrapper; Kestrel binding also protects against a race after that probe.

The command accepts only Development, the fixed localhost `laundry_plant:15433` database and existing demo tenant/plant registration. It rejects additional configured Kestrel endpoints. Its only listener is trusted `https://localhost:7200`; normal certificate/hostname validation remains enabled. A missing/untrusted development certificate is a setup failure, never a reason to disable verification. Pending plant migrations stop the command before server startup; the helper does not migrate, recreate/start containers, initialize orders or download anything.

Process-only overrides enable local order listing and development enrollment, disable scan forwarding and synchronization diagnostics, and suppress framework log providers for this command so only safe summaries reach the console. Normal gateway startup and committed `LocalOrderList:Enabled=false` are unchanged. No new service/dependency, datastore, protocol, production route or operator authentication is introduced.

The helper does not require cloud or Keycloak availability, inspect private environment files, resolve Compose secrets or use a gateway client credential. Its disposable browser credential and enrollment code stay in memory; only normal verifier digests and audit evidence are stored in the plant database. No request cookie, order payload/note text, internal source/enrollment/credential IDs, HTTP error body or list cursor is printed or written to a temporary file. Run correlation IDs and counts are safe diagnostics.

## Workflow

1. Confirm anonymous order-list access returns 401.
2. Create a fresh pending browser enrollment through the existing Development endpoint. Before claiming it, verify its scope/kind/pending status and possession of its valid one-time code. Never select an existing active browser station or accept a source ID as a command argument.
3. Record `VerificationStarted` with a run correlation ID, then exchange the code in the temporary in-memory browser session. Initial enrollment remains scan-only; confirm order listing returns 403.
4. Add only this source's `orders.read` permission and append `PermissionGranted` atomically, updating source configuration version.
5. Page through local summaries (one per page, at most 100 pages), check no-store and exact summary/customer field allowlists, reject duplicate IDs, and compare returned IDs with scoped local copies. This proves access to local data, not cloud freshness. Zero local copies can be a valid access test. Concurrent downloads can invalidate pagination; fail safely and rerun with no concurrent downloads.
6. Remove only the temporary `orders.read` permission with `PermissionRevoked` in the same transaction; confirm listing returns 403 again.
7. In cleanup, remove any remaining test read permission, revoke active credentials and the claimed test source, append corresponding audits atomically, then confirm 401. Stop/dispose the host in all cases.

The only permission-row removal is the grant made for this claimed source. Existing stations and their permissions are not changed. No scans are submitted, no outbox dispatch runs, and orders, downloaded copies, progress/bookmarks and receipts are not changed. Audit/enrollment/source/credential records remain; credentials and source become revoked. An unexchanged code for a revoked source cannot activate that source and retains its original expiry/evidence.

Audits use `ActorType=development-system`, a fixed local-test actor prefix plus run correlation ID, and reason `local-order-list-verification`. This accurately identifies a local setup command, not an authenticated human. `VerificationStarted`, `PermissionGranted` and `PermissionRevoked` extend the existing string-backed source audit action vocabulary; no schema change or migration is needed. These additions do not authorize a general permission-management endpoint or production privilege grants. Real operator/administrator identity and its offline policy remain separate work.

## Failure and cleanup limits

Cleanup has its own 15-second deadline even if the two-minute verification is cancelled. Per-request HTTP timeout is ten seconds. Grant/removal and cleanup each use a database transaction; lost grant acknowledgement still reconciles by removing any committed temporary grant. Cleanup locks the enrollment then source in the same order used by exchange, preventing an in-flight exchange from reactivating a just-revoked source.

The summary reports `passed`, `cleanupComplete` and order count with a run correlation ID. A database failure during cleanup is **not** reported as successful revocation. Investigate claimed-source audit records using the correlation ID before assuming access is gone. No broad cleanup of other sources is permitted.

If the initial enrollment-creation response is lost or fails before the new code/source can be verified and claimed, the command cannot safely identify an enrollment to revoke. It conservatively reports incomplete/uncertain cleanup, grants no order-read permission and does not guess an existing source. A resulting unknown pending enrollment has no active credential; its one-time code expires normally after ten minutes. Its bootstrap evidence may not carry the test run ID because the claim was never recorded. The helper does not claim that a hard process/PC crash can execute `finally`; unexpected termination after a grant can also require explicit correlated recovery. Repeating the helper creates a new test source/run; it does not silently sweep earlier incomplete runs.

## Intentional local execution

Prerequisites: Docker's existing plant database running, explicit plant migrations applied, trusted local HTTPS development certificate and port 7200 free. Existing downloaded copies are used; cloud/identity services are unnecessary for this check.

From the repository root:

```powershell
.\deploy\local\Test-DevelopmentLocalOrderAccess.ps1
```

The PowerShell wrapper restores inherited/absent environment state and its working directory on success or failure. Its nonzero result retains any cleanup warning; do not turn failures into success by suppressing the warning. The test is not a way to permanently enable the endpoint or authorize your normal PWA browser.

## Local live verification

With approval on 2026-10-17, ran the helper once with the plant database already running and gateway ports free. Real trusted HTTPS/Kestrel verification reported `passed=True`, `cleanupComplete=True`, `ordersRead=3`, exercising anonymous denial, scan-only denial, temporary authorized paging, removal/denial and final revocation/denial.

Database checks found one claimed source and one correlated audit each for VerificationStarted, PermissionGranted, PermissionRevoked, CredentialRevoked and SourceRevoked. Attribution matched the Development test actor/reason. There were zero non-revoked claimed sources, active claimed credentials or remaining claimed `orders.read` permissions. Audit and inert source/enrollment/credential evidence remain rather than being deleted.

Full-row aggregate counts/digests matched for every pre-existing row across all 12 plant tables and all 20 cloud tables. Existing sources/credentials/permissions/enrollments/audits were compared by their pre-run identifiers without displaying their contents. Downloaded copies/progress/receipts, migration history, 17 scans, 17 outbox records and cloud records are unchanged. Only new test security evidence was added. No listener remained on 7200/5200 afterward, and no existing process/container was restarted. No forwarding, cloud/identity request, private environment-file inspection, migration or business mutation occurred. The helper and SQL/security/diff checks passed; automated suites were not rerun for this operational-only run.

## Isolated verification

Tests use disposable PostgreSQL and synthetic TestServer browser sessions only. They cover successful grant/read/removal/revocation, preservation of existing sources/business data, repeated runs retaining audit evidence, failure before enrollment, lost creation/exchange responses, failure before exchange/after grant/after removal, cancellation, lost grant commit acknowledgement, an in-flight exchange/cleanup race, explicit cleanup-database failure, fixed installation guards, non-Development rejection and forced forwarding-off overrides. Synthetic PowerShell checks cover normal/failing runs, an occupied port and restoration of inherited/absent environment variables.

Commands (repository root):

```powershell
dotnet test apps/edge/Laundry.Edge.Tests/Laundry.Edge.Tests.csproj --no-restore -p:OutputPath=bin/OrderAccessVerificationFinal --logger "trx;LogFileName=order-access-verification-final.trx" --results-directory artifacts/order-publication-tests
dotnet test tests/synchronization/Laundry.Synchronization.Tests/Laundry.Synchronization.Tests.csproj --no-restore -p:OutputPath=bin/OrderAccessVerificationSync --logger "trx;LogFileName=order-access-verification-sync.trx" --results-directory artifacts/order-publication-tests
.\tests\management\Test-LocalOrderAccessHelper.ps1
git diff --check
```

Focused .NET formatting verification and PowerShell parsing also cover the new files. Final results and checks not run are recorded in `PROJECT_STATUS.md`. This isolation does not replace separately approved live TLS/Kestrel/plant-database verification.

Implementation results: **251 full gateway tests** pass, including **21 new verification/command cases**; the existing cross-component synchronization test and **four synthetic PowerShell cases** also pass. Native CLI smoke checks rejected Production and combined verification/download commands before database/server access. Formatting, PowerShell parsing and diff checks pass. Unchanged full cloud/contract/client/station/browser suites were not rerun. The subsequent approved live run is documented above; it does not permanently activate order listing or authorize an existing PWA browser.
