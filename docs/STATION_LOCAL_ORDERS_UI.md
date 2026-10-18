# Station local-order list

Implemented 2026-10-17 as a Development-only, read-only slice. This is not order selection, operator authentication, receiving or dispatch.

## Behavior

- Once gateway readiness and browser enrollment are established, the PWA uses the generated client to read `GET /api/order-plans`, ten summaries at a time. There is no automatic polling or cloud download.
- Show order number, customer name/code, planned dates and planned status. Do not show notes, customer references or internal identifiers. Rows are not interactive.
- **Refresh list** restarts from the first page; **Show more** appends a validated continuation. “Orders shown” counts displayed rows, not all orders at the plant.
- Last download-page activity is localized with an explicit Stockholm timezone. It does not mean the copies are current, complete or connected to the cloud. Date-only order fields retain their calendar date in both languages.
- Distinguish loading, no download yet, checked-empty, denied access, disabled feature, changed list and unavailable gateway. Never turn an error into an empty-success result.
- Failed refreshes retain previously displayed rows with an explicit warning. Changed/invalid continuations require restarting the list. Authentication/access/disabled responses clear displayed copies; 401 also invalidates the parent station session.
- Requests have a ten-second timeout. Unmounted or superseded responses cannot update the view. Language changes translate existing state without refetching.
- Order copies remain in component memory only; this slice adds no persistent browser order cache. Gateway unavailability therefore does not provide a usable offline order list.

## Access and rollout

The [gateway access contract](LOCAL_ORDER_LIST_API.md) is unchanged: the route defaults off and an enrolled browser needs separate `orders.read`. Ordinary enrollment grants scans only. The earlier temporary-source verification did not authorize normal browsers. No permission grant, service restart or live database change is part of this UI implementation. Controlled live browser activation remains a separate reviewed step; production also needs authenticated operators.

## Live local verification (2026-10-18)

After explicit approval, built the hosted assets and started only the test gateway on trusted `https://localhost:7200`, using process overrides for Development, `LocalOrderList:Enabled=true`, `Forwarding:Enabled=false` and dedicated station `949e3abc-7f01-4379-9d6e-b839b628a710`. No committed defaults changed and no automatic download ran. The ordinary `Start-DevelopmentGateway.ps1` enables forwarding and is **not** the equivalent of this isolated verification startup.

The in-app browser had an earlier enrollment. A temporary ignored setup page used the normal creation/exchange endpoints to enroll a new browser source; only this browser's cookie was replaced. The old source/credential was not revoked or edited. The setup page was removed after enrollment. Before granting, the new source was active with one credential, redeemed enrollment and scan-only permission; the real UI showed denied order access. A guarded local transaction then added only this new source's `orders.read`, incremented its configuration version and appended `PermissionGranted` with Development-system attribution, reason `local-order-browser-verification` and correlation `b80f5aa6-5a79-43f2-92f6-2afee419e034`. It does not claim authenticated human audit identity.

Verified trusted HTTPS readiness 200, anonymous orders 401, new-source denial before the grant, and all three actual downloaded demo summaries after it. Swedish/English, calendar dates, actual download activity and manual refresh worked. There was no scan submission or order mutation. Full-row aggregate counts/hashes matched every pre-existing row in all 12 plant tables; only the new test security records were added. No cloud/identity request, migration or private environment-file inspection was needed. TypeScript check and normal Vite build passed; automated suites were not rerun for this operational-only activation.

The gateway and authorized in-app browser tab are left available for review. **This is browser-specific access:** another browser still has its own source and is not automatically authorized. The dedicated source retains both its original raw-scan permission and the approved order-read permission; no operator workflow is authorized. Stopping the gateway ends serving the route but does not remove the database grant. The standard Development browser credential expires after 30 days; earlier removal requires an explicit audited permission removal or source/credential revocation. Keep the station ID and audit correlation above for that targeted operation, never broaden/revoke other stations as cleanup. Do not treat this Development source setup as production operator access management.

## Design and verification

### User-browser follow-up (2026-10-18)

The user requested access from their own browser and supplied only its source ID from the authenticated `/api/source-session` response. It was already enrolled, active, scoped to the demo tenant/plant and scan-only. A guarded transaction added only that source's `orders.read`, advanced its configuration version and appended `PermissionGranted` under Development-system attribution, reason `user-requested-local-order-read`, correlation `cc7c8c3e-3135-457b-a3fa-26c8cd4dc4db`. No new enrollment, credential, credential lifetime or other-source grant was introduced. The permission remains until explicitly removed or source/credential validity ends. The user's refresh confirmed three visible orders in their own browser. Existing business records, credentials, enrollments and other sources matched aggregate pre/post hashes. The separately authorized in-app test source retains its earlier grant; neither grant authorizes operator workflows.

The station language selector's arrow was also corrected with equal edge inset, retaining native keyboard selection and a forced-colors native-arrow fallback. TypeScript check, all 63 tests and hosted Vite build pass. Desktop Swedish/English, keyboard language change and 390px reflow were reviewed; forced-colors emulation/screen-reader checks were not performed. Cloud management was started with the normal `-WithOrders` helper and verified ready; gateway forwarding stays off.

Applied the Apple design and UI polish skills with the existing restrained dark laundry palette: stable controls and reserved list/notice space, non-clickable rows, visible keyboard focus, semantic labels, text/icons alongside state colors, neutral information, amber setup/change warnings and coral failures. Reduced-motion CSS removes button transitions; narrow layouts reflow without introducing a separate phone workflow.

Station verification: TypeScript build check, all **63 Vitest tests** and an isolated Vite production build passed. Tests cover validation/privacy, errors/timeouts, pagination, retries, changed snapshots, late responses, translation and station invalidation. Built UI was reviewed using synthetic fixtures in English/Swedish, desktop and 390px reflow, access/disabled/empty/unavailable states, paging, changed-list and retained-row refresh failure. Actual reduced-motion/increased-contrast emulation, zoom/screen-reader review and a real authorized gateway browser session were not completed. Unchanged .NET suites were not rerun for this UI-only slice.

To reproduce the synthetic visual preview without real services:

```powershell
# From apps/station-pwa:
.\node_modules\.bin\vite.cmd build --outDir ../../artifacts/station-orders-preview
# From the repository root, in another terminal:
node tests/ui/preview-station-orders.mjs
```

Open `http://localhost:49173/?state=ready`. Fixture states also include `denied`, `disabled`, `unavailable`, `empty`, `not-downloaded`, `loading`, `paged`, `changed` and `refresh-failure`. The banner and QA names identify synthetic data; dates in 2030 are intentional fixtures, not current laundry records. The loopback-only server never proxies to real services and rejects other API operations. Stop it with Ctrl+C after review. Generated preview files are ignored artifacts, not production data.
