# Development scan-to-order preview

Implemented 2026-10-18. This extends the [synthetic item lookup](DEVELOPMENT_ITEM_LOOKUP.md), not production textile registration.

## Behavior

One deliberate simulated RFID read recognizes a fixed synthetic item/customer and finds that customer's downloaded planned orders at the gateway. The browser does not supply customer, tenant, plant or station identity.

- Unknown tag: stop without guessing a customer or querying orders.
- No matching local copies: explain that this does not prove no cloud orders exist.
- One matching plan: display it as a preview automatically, not as an active working order.
- Several matching plans: require an explicit preview choice; never guess by date or list position.

The fixed fixture chooser is a temporary development harness, not a separate mandatory production lookup step. The standalone local-order list and raw-observation simulator remain separate.

Preview choices exist only in component memory and clear on a new read, fixture change or reload. They do not implement persistent order selection, customer switching, an optional working-context lock, receiving activation, quantities or reservation. Orders remain shared. Operator authentication and the confirmed first-receipt transaction remain future work.

## Receiving-date presentation (2026-10-18)

User-approved grouping for matched preview orders:

1. **Today**: planned receiving date equals the plant's current date.
2. **Upcoming**: future planned receiving dates, earliest first.
3. **Overdue receiving**: a separate labelled section for earlier receiving dates, oldest first. This does not declare a missed delivery or change an order's stored status.

Ties use planned delivery date, natural/numeric order number, then immutable ID, ascending. Sorting copies the complete bounded match result; it never drops candidates or chooses among multiple matches. The standalone informational local-order list/API cursor ordering is unchanged. Headings/counts and empty-group explanations remain visible after a recognized match; overall empty/error states stay distinct.

The development plant uses `Europe/Stockholm`, independently of browser language/timezone. ISO date-only plans are compared without timezone conversion. The local formatter accounts for winter/summer offsets. The page updates its date on each simulated read, focus/visibility return and once a minute while mounted. Existing preview choice is retained during regrouping; no network/workflow action is triggered. Production plant-timezone configuration remains future work, not an assumption that all laundries are Swedish.

Verification: **149 station tests** pass, including unsorted input, all groups, ties, unchanged input, empty groups, year boundaries, winter/summer midnight, DST transitions, focus return after midnight, bilingual headings and no automatic selection. Station typecheck, hosted Vite build and diff check pass. Live dark-theme English/Swedish review covered upcoming/overdue, empty Today, keyboard scrolling and 390px reflow without horizontal overflow; viewport restored. Populated Today and rollover have automated coverage, not visually seeded live data. Backend suites and preference/screen-reader checks were not rerun for this presentation-only change. No backend, contract, storage, permission or process setting changed. Apple design/UI polish guidance informed heading hierarchy, stable shared scroll-region size, text plus amber overdue indication and stable focus/pressed styles; no new motion was added.

## Contract and security

`POST /api/development/scan-preview` uses the strict RFID identifier body from item lookup. It is mapped only in Development when scan acceptance, `DevelopmentItemLookup:Enabled`, `DevelopmentScanPreview:Enabled` and `LocalOrderList:Enabled` are enabled. Committed preview flags default off.

The route requires the existing active browser source, both `scans.submit` and `orders.read`, antiforgery validation and trusted loopback HTTPS `https://localhost:7200`. It grants no permission. Original lookup JSON/body/query bounds apply. Identifiers stay out of URLs and responses. Responses use `no-store` and vary by Cookie.

The result contains exactly `isSynthetic`, `status`, `item`, `orders` and nullable `lastSuccessfulPageAtUtc`. Status is `notRecognized`, `noLocalOrders`, `onePlannedOrder` or `multiplePlannedOrders`. Items are explicitly synthetic; order summaries come from actual local downloaded copies. Notes, customer references, credentials and tenant/plant IDs are excluded. Download activity is not proof of freshness or cloud connectivity.

Matching enumerates the existing scoped, validated local-list reader on the server, including continuation pages rather than only the visible browser page. Protected cursors fence progress revisions. Changed-list results return 409; invalid copies/storage failures return 503, never successful empty or partial matches. Development safeguards allow at most 100 pages of 50 summaries and 50 matching plans; exceeding either returns 503 `development_scan_preview_limit_exceeded`. These are demo bounds, not a production lookup design.

No cloud request, migration or business write is performed. The generated-contract client uses fresh session/antiforgery credentials, a ten-second total deadline, cancellation/late-result fencing and strict runtime allowlists/customer/status consistency checks. Manual retry is required after failures; previous results are cleared.

## UI and verification

English/Swedish copy distinguishes preview from receiving. Apple design and UI polish skills informed stable notice/detail space, a fixed-height scrollable plan region, inset select arrows, keyboard focus and a persistent pressed state that does not change size on hover. Read results update without animation; the existing dark laundry palette is retained.

Commands run: full gateway `dotnet test` (309 passed, disposable Docker databases), station `vitest run` (135 passed), station `tsc -b`, generated-client `tsc --noEmit`, OpenAPI generation, hosted/isolated Vite builds and `git diff --check`. Sandbox Vitest could not access temporary files; the host rerun passed.

Live HTTPS browser checks covered multiple matches, explicit preview, empty/unknown, language changes, keyboard activation and 390px reflow without horizontal overflow. Single-plan and error/cancellation/boundary cases pass automated tests; their separate synthetic visual page was blocked by browser policy and not visually reviewed. Screen-reader, actual forced-colors/reduced-motion emulation, hardware and production deployment checks were not run. Only the supported dark theme was reviewed. Unchanged cloud/management suites were not rerun.

Only the identified development gateway was restarted, with all three preview flags enabled for its process and forwarding disabled. No enrollment, permission, credential, migration, order edit or download was performed. All rows/counts/aggregate fingerprints across the 12 plant tables were unchanged after live tests. The gateway remains available at `https://localhost:7200`; refresh and use **Simulera RFID-läsning**, not the separate raw-scan submission button. The temporary synthetic visual server was stopped.

Next: user reviews this read-only flow before agreeing the next receiving/working-context slice. No production order selection or operator authorization decision is implied.
