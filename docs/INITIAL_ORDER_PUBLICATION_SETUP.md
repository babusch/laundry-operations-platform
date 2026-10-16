# Explicit initial order publication — Development only

Date: 2026-10-16. Implemented, tested and **explicitly run against the local Compose database** after both prerequisite migrations. Three eligible planned orders now have version-1 publication heads/outbox records; original records were verified unchanged. A second run created nothing and preserved all publication records. The updated development API was subsequently started with approval using `Start-DevelopmentManagement.ps1 -WithOrders`; trusted HTTPS `/management` returned 200. No gateway delivery occurred. This local approval does not authorize execution against other databases.

## Purpose and boundary

Prepare the first filtered gateway-ready copy of an existing planned order when no published copy exists. This does not create an order, change its notes/dates/version/audit/receipt/number, receive or dispatch textiles, or deliver data to a gateway. Existing published copies are left untouched, even if source data has changed. Independent customer-label update propagation and gateway delivery are separate work.

This is an explicit local maintenance command, not an HTTP route, login action, background task or production administration endpoint. It does not impersonate `management-demo` or construct a verified-human identity. It uses the local setup operator's OS identity, prefixed `local-development-initial-publication:`, solely as immutable cloud provenance on newly created outbox records. Normal order-save records keep this optional field null and retain their existing human order audits. Historical null provenance is not proof of a human or creation path; no attribution is invented. Production administration would need its own authenticated/authorized design before this tooling can be generalized.

## Guards and prerequisites

- The application flag is `--initialize-development-order-publications`; it cannot be combined with the customer/membership setup flags or repeated.
- Before opening a connection, the command requires Development and the named `laundry` database at `localhost` or `127.0.0.1`, port 15432. It always selects the existing synthetic development tenant/plant constants; configuration does not select another scope. The lower-level runner accepts scope values for composition/tests, not as an externally authorized API.
- Order and customer migrations must already be applied. The command checks pending migrations, refuses rather than migrating/seeding/granting, and never starts/recreates containers. It does not need Keycloak or a user password.
- Required order migrations are `20261016194052_OrderPublicationStorage` and `20261016200957_InitialPublicationAttribution`; both are applied locally. The latter adds nullable varchar(200) `initial_publication_actor` to immutable cloud outbox evidence. Existing rows remain null; no actor identity or order copy is backfilled by migration. Existing append-only triggers protect this metadata as well.
- Stop the development API before applying migrations/building the helper's normal output. Startup itself never migrates or runs initial publication.

## Prepared commands — execution requires separate approval

After approving and completing the migration step, the explicit initial-publication helper is:

```powershell
.\deploy\local\Initialize-DevelopmentOrderPublications.ps1
```

The helper only runs the guarded app command, with `--no-launch-profile` and explicit Development. It does not read private environment files itself or print credentials. The application can consume its normal configuration internally. The CLI exits instead of starting the web server, so a subsequent separately approved management startup still uses `Start-DevelopmentManagement.ps1 -WithOrders`.

Migration preparation for another database, when separately approved, uses the existing `Initialize-DevelopmentOrderStorage.ps1` helper; it applies pending order migrations without publishing orders. This helper has now completed successfully locally. Do not use initial publication as a substitute for migration. No automatic grant or membership initialization is part of either operation.

## Batching, privacy and retry behavior

The command enumerates scoped planned orders missing a publication head in pages of at most 100, ordered by stable order ID. Each order has its own short transaction. It takes the shared plant writer lock, rechecks whether another writer published the order, locks the current order, and resolves eligible customer labels/plant service under the same transaction. An inactive/missing customer or inactive/missing service is counted and skipped without an outbox write or business mutation.

It then stages only a missing version-1 copy through the same allowlisted mapper and stores head, outbox and setup attribution atomically. Hidden text is null, never forwarded in metadata; the attribution is not part of the gateway JSON. Scope identity in a payload alone does not authorize this action: local command/connection access is the Development maintenance trust boundary.

Existing copies are excluded during enumeration and rechecked after locking. `alreadyPresent` counts races found at that second check, not the total number of already published orders. Output contains a per-invocation run correlation ID and counts: examined, created, already present, ineligible customer and unavailable order. The run ID correlates console diagnostics, not an authenticated person or persisted batch checkpoint. Skipped orders remain in the source database, and a later run from the beginning can publish them after eligibility is corrected. Finishing a sweep does not mean every skipped order is published or any gateway has received it.

If a later order fails, previously committed copies remain. The command exits nonzero with a safe message, no credential/order payload/error body, and instructions to rerun from the beginning after correcting the prerequisite. A lost acknowledgement or process/database restart can therefore be recovered without duplicate outbox IDs, changed old payloads or version increases for existing copies. Never clear heads/outbox to make a retry work.

This is a bounded sweep, not a frozen snapshot of all orders: an import/concurrent insertion behind the current ID cursor can require another full run. Ordinary new creates/edits publish automatically once activated, and concurrent initialization/edits serialize with the same plant lock. Existing later copies are never overwritten by initialization. No expiry/date cutoff, removal inference, retention cleanup, delivery acknowledgement or full reconciliation is implemented.

## Verification

Local activation (2026-10-16): the approved helper examined and published three orders with no skips. Server-side aggregate full-row digests confirmed preservation of three orders, five order audits, five receipts, one number counter, one note default, two default commands, one membership, four permissions and two membership audits. SQL checks confirmed three scoped version-1 heads, matching immutable outbox payloads with setup attribution, zero hidden-note violations and no customer-reference fields. A second helper execution examined/created zero and left original/publication digests unchanged. Both builds/runs passed. No gateway delivery, private credential inspection, grant or API restart occurred; existing automated suites were not rerun for this operational step. The source-verification paragraphs below describe the earlier implementation checks, before live activation.

Focused `dotnet test apps/cloud/Laundry.Api.Tests/Laundry.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~InitialPublication_|FullyQualifiedName~InitialOrderPublicationTargetTests" -p:OutputPath=bin/InitialPublicationFocused` passes all 17 checks. They cover local-target/environment guards, rejected non-Development command execution, prerequisites without writes, scope/privacy, local attribution, preservation of source orders and existing history/receipts/counters, pagination past ineligible records, later recovery, concurrent ordinary editing, partial rollback, lost commit acknowledgement and database restart. Model consistency is checked after applying the additive migration in a disposable test database.

The helper's PowerShell syntax was checked with the PowerShell parser without executing it. The real helper/CLI was not run against the named development database. No private credential values, live schema/orders, permissions or running API were changed. Existing UI/gateway/client paths were not changed; their separate suites/browser checks are not part of this slice.

Full `dotnet test apps/cloud/Laundry.Api.Tests/Laundry.Api.Tests.csproj --no-restore -p:OutputPath=bin/InitialPublicationRegression --logger "trx;LogFileName=initial-regression.trx" --results-directory artifacts/order-publication-tests` passes all 389 cloud tests. After the final console run-correlation refinement, the same focused filter passes all 17 cases again with `bin/InitialPublicationFinalFocused`. All 30 `node --test` cases in `packages/contracts`, focused `dotnet format whitespace ... --verify-no-changes`, and `git diff --check` pass. This is disposable-database/source verification, not live activation.
