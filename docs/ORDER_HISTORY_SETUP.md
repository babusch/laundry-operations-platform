# Order history

Date: 2026-10-15. The protected API/name recording was implemented first, followed by the user-authorized read-only history page.

## Readable history page

Open `/management`, select an order, then **View history / Visa historik**. The Development-only page at `/management/orders/{plantId}/{orderId}/history` reuses the verified session, current scoped `orders.read` and owning order store. It makes no business writes and needs no new permission, migration or dependency.

The page shows the original five planning fields plus recorded operator-note visibility, then only saved changes with labeled Before/After values, ascending version, account-name snapshot and server-recorded time. Visibility uses Shown/Hidden/Not recorded (Visas/Dolda/Ej registrerat); missing legacy evidence is never inferred as hidden. Missing historical account names say Name unavailable / Namn saknas. Dates are localized in Swedish/English; timestamps explicitly use Europe/Stockholm with numeric UTC offset, including daylight-saving changes. Storage and machine-readable time remain UTC. Language is not a timezone selector. See [visibility API follow-up](ORDER_NOTE_VISIBILITY_SETUP.md#api-follow-up-2026-10-15) for the pending local activation boundary.

Customer labels are **current** code/name lookups for referenced IDs only (bounded to at most 51), restricted to the same tenant and plant-service link, including inactive customers/services. They are not historical name snapshots. Missing labels say Customer unavailable; cross-tenant/plant labels are never fetched as a fallback. All values are HTML-encoded; multiline text retains its line breaks.

History pages contain 25 entries, protected scope/order/limit-bound continuation and a first-page link. Language changes retain the cursor. Invalid cursors, missing orders, session/permission loss and database failure have safe recovery messages. New committed versions can appear on later pages; paging does not promise a frozen snapshot.

The Apple design and UI polish skills guide restrained typography, solid dark laundry surfaces, consistent spacing and aligned comparisons. This temporary bridge retains normal document navigation; seamless in-place language changes/scroll restoration are not introduced. Desktop/resized-window reflow is in scope; no separate phone/light theme, undo, receiving or dispatch workflow is added.

## What is implemented

`GET /api/v1/plants/{plantId}/orders/{orderId}/history` reads an order's creation and planning changes from the existing append-only audit. It requires the verified human management session and current `orders.read` permission for exactly one active tenant assignment at the URL plant. Tenant/actor are never selected by query/body fields. Gateway/source credentials do not grant history access.

This uses the existing opt-in Development-only loopback HTTPS boundary at `https://localhost:7100`. It introduces no new service/dependency and does not activate receiving, dispatch, undo, deletion, export or a production rollout. Keep using the single management bookmark and history button; no console exercise is required.

The response is `{ "items": [...], "nextCursor": null }`, with an opaque cursor when another page exists. Each entry contains:

- `version`, `action` (`order_created` or `order_plan_changed`) and server `recordedAtUtc`.
- `actorDisplayName`: the bounded verified account-name snapshot, or null when unavailable. The UI translates null to “Name unavailable”; it must not guess or expose the raw identity as a fallback.
- `initialPlan`: five original planning fields plus nullable recorded `notesVisibleToOperators` for creation; null for changes. Null visibility means not recorded in legacy evidence.
- `changes`: empty for creation; otherwise only changed fields with `field`, `before` and `after` (at most six). Field names are language-neutral; values are strings/null (UUID for customer, YYYY-MM-DD for dates, text/null for reference/notes, literal `true`/`false` strings or unknown/null for visibility).

Internal tenant/actor issuer/subject, audit IDs, request keys and receipt fingerprints are not returned. Planning notes/reference and account names are access-controlled business data, not routine diagnostic logs. The read is no-store, requires neither antiforgery nor an idempotency key, and makes no order, audit, receipt or numbering writes. Customer deactivation does not hide historical evidence. Unchanged saves, rejected work and exact retries add no history entries.

## Paging and failure behavior

Entries sort ascending by order version, not timestamps; this remains stable when clock timestamps tie or move backward. `limit` defaults to 25 and accepts 1–100. `cursor` is protected and bound to tenant, plant, order, limit and the versioned query purpose. Continue with the same limit and URL-encoded `nextCursor`. Unknown/duplicate queries and invalid/tampered/wrong-scope cursors return 400 `invalid_order_history_query`. No filters are supported yet. New committed versions may appear after the cursor; there is no frozen snapshot guarantee. Restart from the first page if cursor protection keys are lost.

No session returns 401; permission/scope denial 403; an order absent from the authorized scope 404 `order_not_found`; ambiguous tenant assignment 409 `plant_scope_ambiguous`; storage/history-read failure a safe 503 `order_history_unavailable`. Disabled/non-Development returns 404, and wrong transport/origin/non-loopback is rejected. Problem Details contain safe codes/correlation IDs, not request data or exceptions; framework authentication/transport responses may be empty. Retry a failed read without creating any business command.

## Verified account-name recording

The OIDC request now includes `profile` alongside `openid`. After normal issuer/audience/signature/lifetime/nonce validation, the backend accepts exactly one string `preferred_username`, trimmed, nonempty, at most 200 characters and without control characters. Missing, ambiguous or invalid account-name data becomes unavailable, not a guessed name or an authentication bypass.

Only that bounded name is copied into a server-owned protected session claim, alongside existing verified identity/provenance. Unrelated token profile fields, email, roles, permissions and arbitrary incoming copies of the server-owned claim are discarded. The name grants no authority. Stable issuer/subject still determine attribution and permissions; an account name is not guaranteed unique or a legal identity. No name is accepted from order JSON, form fields or query strings.

At a successful create/change transaction, the name from that verified session is copied into nullable `orders.order_audit_entries.actor_display_name`. It reflects the provider's account name at login, not a live directory lookup on every save/read. Old sessions without the new claim can still work and record null. Old audit rows remain untouched/null; no backfill from today's names. A renamed account does not rewrite older entries, and retries preserve the originally accepted audit/name. Friendly names are not part of command fingerprints or stable authorization identity.

## Activation prerequisites

Live activation completed (2026-10-15): the user attached the optional profile scope, signed in afresh and deliberately saved the requested test note through the existing edit page. A read-only latest-audit metadata check confirms `order_plan_changed`, version 3, with a non-null name matching `management-demo`. This verifies real login-to-audit account-name capture without exposing tokens, raw identity subjects or note contents. The agent did not mutate the order. The migration was already applied as recorded below. No code tests were rerun for this verification-only step; history API regression tests remain the previously recorded passing suite. The history page is still the next separate slice.

Profile-scope update (2026-10-15): the user confirmed `profile` was absent from the existing management client and they added it following the Optional-scope instructions. Fresh login and actual account-name capture remain to be verified; no successful new audit/name entry has been claimed merely from this report.

Live migration update (2026-10-15): the user authorized activation and the existing helper successfully applied `20261015173629_OrderAuditAccountName` to the local cloud database. The stopped existing container was started, not recreated. Before/after aggregate counts are unchanged at two orders, three audit entries and three command receipts; the nullable varchar(200) column and four append-only triggers were verified. No API was listening on port 7100 and no order creation/edit was performed. **The database prerequisite is complete.** The optional Keycloak profile scope and fresh-login claim proof remain pending.

The additive `OrderAuditAccountName` migration adds one nullable varchar(200) column. Existing order/audit/receipt data and append-only triggers are preserved. Normal startup still does not migrate automatically. **Apply this migration before running the new API build**, because order writes now include the new audit column.

When ready for live activation, stop the API in its own terminal, then use the existing explicit helper from the repository root:

```powershell
.\deploy\local\Initialize-DevelopmentOrderStorage.ps1
```

The helper applies OrderDbContext migrations only; it does not create an order or reset existing records. Keep Docker running. No password/private environment-file inspection is needed.

The credential-free `deploy/local/management-development-client.json` now attaches the built-in `profile` client scope as optional, with no default scope or application permission change. **An existing Keycloak client is not updated by editing that file.** Before the next fresh login, ensure `management-development` in `laundry-development` has `profile` attached as an optional client scope. Do not recreate clients/volumes, change gateway credentials, grant administrator roles or regenerate secrets. An absent username claim must remain unavailable; do not add an unauthenticated lookup or manually supplied fallback. Live scope configuration/token inclusion is still unverified and will be guided separately.

After prerequisites are satisfied, restart through `Start-DevelopmentManagement.ps1 -WithOrders` and sign in afresh to capture the account name. The API-only implementation did not apply live changes; the subsequent user-authorized activation has now applied the migration as recorded above. It has not yet attached the scope, restarted the API or changed live orders. The history page and live user review come next.

## Verification

History-page verification (2026-10-15): `dotnet test LaundryOperations.sln --no-restore -p:OutputPath=bin/HistoryPageFinalVerification/` passed all 408 then-present tests (274 cloud, 133 gateway, one cross-component). After final navigation polish and an additional customer-label isolation test, the focused history-page/transport run with `bin/HistoryPagePolishVerification/` passed all 11 cases. Coverage includes bilingual creation/changed-only comparisons, missing/encoded account names, multiline encoded notes, inactive and unavailable customer labels, bounded tenant/plant lookups, session/current read permission, missing/scoped orders, 25-entry continuation, cursor tampering/order binding, disabled/transport boundaries, and database outage/recovery without business writes. The sandboxed first test attempt stalled on Docker access and was interrupted; the successful runs used disposable Docker databases. Initial failing assertions exposed the disabled-history response and two test assumptions (antiforgery adds no-cache, and Docker restarts can change mapped ports); these were corrected before passing runs.

The agent restarted only the verified local API through the existing helper and reviewed actual HTTPS history in Swedish/English, initial/changed entries, missing/recorded names, keyboard language submission, 1000px resized desktop reflow without horizontal overflow, and invalid-link recovery/back navigation. No live order was created or edited. Apple design/UI polish guidance was applied to typography, wayfinding, aligned comparisons, fixed-size controls and non-animated presentation. Screenshot: ignored `artifacts/ui-review/order-history-sv.png`. Live multipage/empty/outage/permission-error visual review, enlarged text, forced colors and screen-reader narration remain unverified; automated coverage does not substitute for those checks. No station/client/API contract/dependency changed in this UI slice, so their separate checks and migrations were not rerun. Focused formatting and diff checks are recorded with the handoff.

The following API-only verification and activation notes are historical; the later live activation and history-page sections above supersede their pending-work statements.

Commands/results (2026-10-15): `dotnet test LaundryOperations.sln --no-restore -p:OutputPath=bin/HistoryFinalVerification/` passes all 403 tests (269 cloud, 133 gateway, one cross-component). The earlier focused history/login run passed all 48 then-present cases; the final suite additionally includes max/default paging and forged-name/unknown-order checks. `corepack pnpm --filter @laundry/api-client generate:orders`, package type checking including compile-only nullable/public-history assertions, `corepack pnpm test:station` (25 tests plus client/station type checks), and `corepack pnpm test:contracts` (20 tests) pass. Focused solution whitespace formatting with `--verify-no-changes` and `git diff --check` pass. Rebuilt `dotnet ef migrations has-pending-model-changes --project apps/cloud/Laundry.Api --context OrderDbContext -- --environment Development` reports no pending changes. An initial `--no-build` attempt used the pre-generated-snapshot assembly and was superseded by that rebuilt check. Unchanged startup helpers were not rerun. No UI/browser test is claimed for this API-only slice.

Tests use disposable databases, synthetic signed ID tokens and synthetic orders only. They cover name provenance/minimal session claims, invalid/ambiguous/missing names, old audit migration preservation, append-only protection, actual OIDC login through create/edit/history, public changed-field projection, immutable account-name snapshots through retries/renames, no-change/rejected writes, bounds/continuation/scope tampering, current read permissions, deactivated customer history, outage/recovery and Development transport/authentication boundaries. OpenAPI and generated TypeScript history contracts are in the existing separate order client package. Live schema application, real Keycloak profile inclusion and browser history rendering remain unverified.
