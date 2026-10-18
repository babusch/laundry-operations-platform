# Development-only item lookup

Latest follow-up: the PWA now combines recognition and matching downloaded plans in one [read-only scan-to-order preview](DEVELOPMENT_SCAN_PREVIEW.md). The original lookup endpoint remains a Development foundation; the lookup-only UI description below records the earlier slice.

Implemented 2026-10-18. This is a synthetic item/customer lookup foundation, not a production textile registry, ownership-management feature or receiving workflow. The station now has a separate lookup-only panel; see the UI follow-up below.

## Scope

`POST /api/development/item-lookup` resolves a fixed RFID fixture at the gateway. The catalog lives in `apps/edge/Laundry.Edge/Textiles`, behind a clearly Development-specific endpoint. No cloud service, scanner SDK, new dependency, database table or migration is introduced. The endpoint reads local source authorization but performs no database writes. It never submits an observation/outbox event, selects/locks an order, activates receiving, increments quantities or changes ownership. Repeats, concurrency and input ordering have no business effect.

The route is mapped only in Development with `ScanAcceptance:Enabled=true` and explicit `DevelopmentItemLookup:Enabled=true`. The committed Development default is **false**. This implementation did not restart/activate the running gateway, change its flags or grant any permission. Local activation should be intentional, not achieved by changing committed defaults.

Require trusted loopback `https://localhost:7200`, an active enrolled browser source and its existing `scans.submit`, plus the normal antiforgery header obtained from `/api/source-session`. Present Origin/Fetch Metadata headers must identify the same origin. No caller-supplied tenant/plant/customer is accepted. Source identity is resolved from the existing credential; the catalog additionally accepts only the fixed synthetic demo tenant `11111111-1111-4111-8111-111111111111` and plant `22222222-2222-4222-8222-222222222222`. A different installation gets a forbidden result, not demo ownership data. `orders.read` is neither required nor granted: this endpoint reveals only fixed synthetic lookup fixtures, never orders or production item data. Production item-read permissions/operator policy require separate design before replacing the fixtures.

## Fixtures

| Exact RFID value | Item | Article-type code | Customer |
|---|---|---|---|
| `SIMULATED-RFID-TOWEL-001` | Synthetic towel 001 | `towel` | Demo laundry customer (`DEMO-001`) |
| `SIMULATED-RFID-SHEET-001` | Synthetic sheet 001 | `sheet` | Demo laundry customer (`DEMO-001`) |
| `SIMULATED-RFID-MOP-001` | Synthetic mop 001 | `mop` | Synthetic cleaning team (`SYNTHETIC-TEAM`) |

The first customer's fixed ID matches the existing synthetic demo customer. The second customer is a fixture only: this does not create a cloud customer, service eligibility, continuous account or order for it. Article-type codes are fixture metadata, not persisted article definitions. Every result is explicitly marked `isSynthetic: true`. The existing random scan simulator does not automatically generate these known tags and its scan submission behavior is unchanged.

## Contract

JSON request (maximum 1024 UTF-8 body bytes, including unknown-length/chunked bodies):

```json
{"identifier":{"technology":"rfid","value":"SIMULATED-RFID-TOWEL-001"}}
```

Only these exact properties are allowed; duplicate/unknown properties and all query parameters are rejected. This slice supports RFID only. Value length is 1–256 characters, case-sensitive, without surrounding whitespace/control characters. No normalization or inferred ownership is performed. Identifiers are in the body, never a URL, and are not echoed or logged by the handler.

Recognized result, HTTP 200:

```json
{
  "isSynthetic": true,
  "status": "recognized",
  "item": {
    "id": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1",
    "displayName": "Synthetic towel 001",
    "articleType": "towel",
    "customer": {
      "id": "33333333-3333-4333-8333-333333333333",
      "code": "DEMO-001",
      "displayName": "Demo laundry customer"
    }
  }
}
```

Unrecognized result, also HTTP 200:

```json
{"isSynthetic":true,"status":"notRecognized","item":null}
```

This means only “not recognized in these fixtures,” not “unregistered in the real laundry.” It must not select a customer or invent/register an item. Storage/authentication failures must not be presented as unrecognized tags.

| Status | Meaning |
|---|---|
| 400 | Invalid JSON/shape/value/query, unsupported technology or missing/invalid antiforgery |
| 401 | Missing/invalid/expired/revoked/disabled/wrong-scope source |
| 403 | Transport/origin forbidden, not a browser, missing scan permission or non-demo fixture scope |
| 404 | Feature disabled, scan acceptance disabled or not Development |
| 413 | Body exceeds 1024 bytes |
| 415 | JSON content type required |
| 503 | Local source-identity storage unavailable; retry after recovery, not an unknown item |

Handler responses use `Cache-Control: no-store`, `Vary: Cookie` and fixed error codes without input/error details. The storage warning logs only a correlation identifier. No caching/reference-data synchronization or browser emergency mode is added. Cloud/Keycloak availability is not needed once a source is enrolled; local source-identity storage remains necessary.

OpenAPI is in the existing station schema; the regenerated `createGatewayClient` supports the operation. `DevelopmentItemLookupRequest` and `DevelopmentItemLookupResult` are exported generated types, not persistence entities or hand-maintained browser DTOs.

## Verification and next boundary

All **290 gateway tests** pass, including **39 new lookup cases**, using disposable PostgreSQL/TestServer only. Coverage includes the three fixtures, unknown/case-sensitive input, scope/permission/source boundaries, origin/TLS checks, explicit activation, JSON/byte limits including unknown-length bodies, antiforgery, stable retries/concurrency/out-of-order calls, unchanged operational counts and identity-storage outage/recovery. All **63 station tests**, generated-client and station TypeScript checks, focused C# whitespace formatting and `git diff --check` pass. No live database, credentials, permissions, order copies, running service or UI was changed. Full cloud, cross-component synchronization and real-browser/live endpoint checks were not rerun for this isolated API slice.

Commands run:

```powershell
dotnet test apps/edge/Laundry.Edge.Tests/Laundry.Edge.Tests.csproj --no-restore -p:OutputPath=bin/DevelopmentItemLookupRegression --logger "trx;LogFileName=development-item-lookup-regression.trx" --results-directory artifacts/item-lookup-tests
# In packages/api-client: installed openapi-typescript regenerated station-api.ts; tsc --noEmit.
# In apps/station-pwa: installed tsc -b --pretty false; vitest run.
git diff --check
```

The lookup-only simulator integration described below is now implemented and live-verified. Do not silently connect lookup to `/api/scans`, order selection, locks or receiving. Real business actions still need authenticated operators and explicit workflow context; production item ownership/storage/publication remain later reviewed work.

## Lookup-only station panel (2026-10-18)

The enrolled station now displays **Item lookup preview / Förhandsvisning av artikelsökning** above the separate raw-scan simulator. Choose a towel, sheet, mop or unknown test tag, then press **Look up item / Sök artikel**. All options remain available. A change clears the previous lookup; it does not issue a request until the button/form is submitted. No arbitrary real-tag input is offered. Fixture names/customer names are returned test data; interface labels, article-type labels and feedback translate immediately between Swedish/English without refetching or resetting the choice.

The adapter uses only generated `GET /api/source-session` followed by `POST /api/development/item-lookup`, with fresh antiforgery, included browser cookies, no-store and one ten-second deadline for the pair. It never calls `/api/scans` or order APIs. Strict runtime allowlists validate synthetic markers, recognized/unknown consistency and item/customer fields; malformed/unexpected/private fields fail closed as unavailable. No server error detail, internal ID, token or cookie is displayed, logged or persisted by the panel.

Recognized results show synthetic item name, customer/code and localized article type. Unknown results explicitly avoid guessing ownership. Unavailable, disabled, forbidden and rejected requests have distinct translated feedback and clear old details, rather than showing an earlier customer's result under a new tag. Source 401 invalidates parent enrollment, hiding lookup/scanning/order views until setup is restored. Requests are manual, no automatic retry/polling; input stays usable while loading. Changing the choice/unmounting aborts and fences late responses. The lookup preview adds no persistent browser item storage and does not decide production order-context persistence.

Apple design and UI polish guidance informed the restrained dark palette, native keyboard select with inset arrow, stable button labels/dimensions, reserved notice/detail space, visible focus and non-color-only framed feedback. Ordinary information/recognition is neutral, unknown/access/setup/rejection is amber and outages are coral. Recognition is deliberately not styled as a saved receipt. No new animation/gesture or light theme was introduced.

### Verification and local activation

All **115 station tests** pass (52 additional adapter/component cases), including generated request shape/credentials/CSRF, errors, malformed data/privacy, deadline/cancellation, late results, manual retry, language changes and parent invalidation/no scan submission. An existing audit privacy assertion was narrowed to its audit region because synthetic tag text is now intentionally visible in the separate lookup panel; audit identifiers remain hidden. Station/client TypeScript checks, hosted and isolated Vite builds and diff checks pass. Initial test typing issues were corrected before the passing final run. The unchanged full backend/cloud/synchronization suites were not rerun for this UI-only follow-up; the preceding 290-test gateway result remains the API baseline.

Stopped only the verified development gateway process, then rebuilt/restarted it in isolated `LookupPanelCheck` output on trusted loopback `https://localhost:7200`. Process-only overrides enable lookup and local orders, retain the existing dedicated station configuration and explicitly keep forwarding off. No migrations, source grants, new enrollment, cloud changes or private environment-file inspection were needed. Normal committed lookup/list defaults remain false. The running gateway and its existing authorized browser sessions are left available for user review.

Actual HTTPS browser lookups verified all three fixtures and unknown, both languages, manual retries and unchanged panel height between recognized/unknown. Synthetic-only preview fixtures covered loading, cancellation, forbidden, disabled, rejected and unavailable states without touching live security/storage. Narrow 390px viewport review showed reflow/no horizontal overflow and stable recognized/unknown height; temporary viewport overrides were reset. Actual forced-colors/increased-contrast/reduced-motion emulation and screen-reader review were not performed. No animation behavior is claimed beyond the static implementation.

Full-row aggregate counts/hashes across all 12 plant tables matched before/after live lookup checks: 17 observations/outbox rows, three order copies, progress, receipts and every security/enrollment/credential row were unchanged. No scan was submitted. The temporary fixture server/tab were stopped/closed; the screenshot is an ignored artifact, not tracked operational data.

For synthetic error-state review, the existing `tests/ui/preview-station-orders.mjs` now supports `lookup-loading`, `lookup-forbidden`, `lookup-disabled`, `lookup-invalid` and `lookup-unavailable`, as well as ordinary recognized/unknown fixture lookup. Build its isolated bundle using the command in [station preview instructions](STATION_LOCAL_ORDERS_UI.md#design-and-verification). It never proxies to real services.

Next small step: the user tries this lookup-only panel. Order resolution, optional locks, operator authentication and receiving remain separate reviewed slices.
