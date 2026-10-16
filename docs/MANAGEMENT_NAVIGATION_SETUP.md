# Development management navigation

The user verified the live order list and approved one bookmarked entry point. This small bridge is server-rendered by the existing API; it introduces no dependency or new service. The long-term management UI remains Next.js in `apps/admin-web` under ADR 0019, not these Development-only pages. No handwritten TypeScript API contract or production hosting change is introduced.

## One bookmark

Stop the running API with Ctrl+C, then run from the repository root:

```powershell
.\deploy\local\Start-DevelopmentManagement.ps1 -WithOrders
```

Open `https://localhost:7100/management` and bookmark it. Before starting the new note-visibility build, explicitly apply the two prepared migrations in [note-visibility setup](ORDER_NOTE_VISIBILITY_SETUP.md); this is not done automatically at startup. The page offers sign-in/status, order navigation, a readable paginated list, planned-order creation/editing, order details, and CSRF-protected local sign-out. Receiving and dispatch are not UI actions yet. Existing JSON routes remain available for diagnostics; users no longer need them for ordinary viewing or planning changes.

Order creation/editing now includes **Show notes to operators / Visa anteckningar för operatörer**, and details show the saved choice. The list's **Order settings / Orderinställningar** link opens the plant default. It is readable with current order-read permission and editable only with separate `orders.notes.default.edit`; initialization does not grant it. Defaults initialize new orders only. These controls are implemented but not yet activated in the local database/API.

## Create a planned order

Choose **View orders / Visa ordrar**, then **Create order / Skapa order**. The link and form require current plant-scoped `orders.create`; a login alone is not permission. Choose an eligible customer and both planned dates. Future receiving dates and same-day delivery are allowed; delivery cannot precede receiving. Customer reference (100 characters) and notes (2000) are optional. Nothing is saved by opening the form, switching language, or cancelling. Saving creates only a plan, then redirects to its detail; it does not start receiving or dispatch.

The native controls retain entries after field validation, language changes and handled storage errors. Existing field feedback is translated on language change. A server-protected draft carries one actor/plant-bound retry key, not an access grant; current permission and customer eligibility are checked by the same transactional storage seam as the JSON API. Double submissions and exact retries return the same order. If a commit result is uncertain, the form locks the original fields and offers **Retry same save**. Do not open a new form to repeat an uncertain save; first confirm its outcome through the original retry or order list. Invalid/tampered drafts must be reopened, and expired sessions must sign in again. Unsaved forms are not a durable offline draft system.

These Development forms show at most 100 eligible customers and warn if truncated. Search, customer defaults, scheduling, production deployment and shop-floor offline order publication remain deferred. Dates submit ISO calendar values; native date-control display follows the browser/OS locale.

## Edit a planned order

Open its detail page and choose **Edit order / Redigera order**. The five planning fields are prefilled; read and planned-edit permissions are required. Save changes returns to details, and Cancel makes no write. Entries and validation messages are retained/translated on language changes. A conflicting save shows latest saved values plus retained entries and requires explicit review before another deliberate save. Uncertain saves retain the original version/key/fields for exact retry. See [planned update setup](ORDER_UPDATE_SETUP.md) for recovery details and boundaries. **View history / Visa historik** opens read-only creation/changed-field evidence; see [history setup](ORDER_HISTORY_SETUP.md). Receiving and dispatch remain unimplemented.

- The home page is available signed out; protected views show a readable sign-in prompt on session expiry, not raw browser 401 text.
- The sign-in link uses `home=true`, selecting the fixed `/management` callback destination. Ordinary login retains `/management/session`. Arbitrary return URLs remain ignored.
- Sign-out is a POST form with the real same-session antiforgery token. It ends only the local application session; copied tickets/provider logout semantics remain as documented in the identity setup.
- Readable order views use the same owning order store, trusted-human access seam, current plant permission, scope resolution, and protected pagination as the JSON API. They never accept tenant/actor from form data. No writes or eligibility reactivation occur while browsing.
- Lists show 25 orders per page and continuation navigation. Details show number, planned dates, reference, notes and version. No internal identity-provider subjects or raw tag values are rendered. Customer names/master catalog integration remain a later slice.
- Database failure, permission loss, missing orders and invalid page links have readable recovery guidance. Error text never contains raw exceptions or planning contents.
- User-provided reference/notes are HTML-encoded. Pages use no-store, no-referrer, nosniff, frame restrictions and a script-free Content Security Policy. All routes inherit Development-only exact loopback HTTPS restrictions.

## Design and localization

The `apple-design` and `ui-polish-rules` skills guide this bridge alongside `UI_DESIGN.md`: existing muted dark blue-green tokens, system typography, large controls, keyboard focus/skip link, consistent spacing, restrained pointer-down feedback, and responsive layout. Errors have coral frames/washes; setup warnings use amber. Only real links/buttons get hover feedback without changing size.

Swedish and English are bundled server-side. A native language form remembers a secure HttpOnly preference cookie, applies it before HTML render, changes document language and formats calendar dates locally. Changing language retains the current order/page cursor. This temporary page uses normal document navigation, not animated transitions or a client-side router; there are no gesture animations to interrupt. It does not introduce a light theme or shop-floor offline behavior.

## Verification boundary

Create-form verification (2026-10-11): `dotnet test LaundryOperations.sln --no-restore -p:OutputPath=bin/CreatePageHandoffVerification/` passes all 353 tests (219 cloud, 133 gateway, one cross-component). After the final validation-message translation refinement, all 19 focused form/navigation/transport cases pass with `bin/CreatePageFinalLanguageVerification/`. Focused formatting and `git diff --check` pass. Tests cover actor-bound drafts, CSRF/tampering/wrong scope, current permissions/customer eligibility, field limits/dates, bilingual retained entries, concurrent duplicates, a real commit followed by lost acknowledgement, and database outage/recovery. No startup helpers, station code, generated clients or dependencies changed, so their separate checks were not rerun.

The agent safely restarted only the verified API using the normal `-WithOrders` helper, without inspecting private environment files. Live English/Swedish desktop form, retained entries, validation/error translation, keyboard language submission, cancel and a 1000px resized-window layout were reviewed. The list still contained only `ORD-000001` after cancellation; no additional live order was created. A fresh Swedish form is left open, with a screenshot under ignored `artifacts/ui-review/create-order-sv.png`. Success redirects, recovery and duplicate saves were tested in disposable databases, not by writing a live order. Enlarged text, forced colors, screen-reader narration and live successful creation remain unverified. The desktop-only scope and normal-document-navigation limitations remain explicit.

Tests cover signed-out/expired navigation, signed-in list/detail rendering, both languages, HTML encoding, wrong-plant rejection, real form antiforgery sign-out and fixed login return destinations. Existing API authentication/storage tests remain relevant.

Verification (2026-10-11): `dotnet test LaundryOperations.sln --no-restore -p:OutputPath=bin/ManagementPageVerification/` passes all 342 tests (208 cloud, 133 gateway, one cross-component). After final markup/color refinements, all five focused page/login cases pass using `ManagementPageFinalVerification` outputs. Focused formatting and `git diff --check` pass. Station/TypeScript tests were not rerun: no station code, generated clients or frontend dependencies changed. Test preview fixtures are generated only under ignored test build outputs and contain synthetic data. The live API was not restarted and no private environment file was inspected.

Desktop polish (2026-10-11): the language selector now keeps a native select with an inline, decorative SVG chevron, matching 1rem left/right edge insets, stable width, and extra text-to-arrow clearance. It requires no image permission or CSP relaxation. Forced colors restores the native arrow. The skip link focuses the main content region. Management is desktop/workstation-first; a separate phone layout is not a current requirement, while resized windows and enlarged text must remain usable.

The live HTTPS page is now accessible to the browser tool. Signed-out and signed-in home, order list and details were visually inspected in English and Swedish. The normal 1280px desktop viewport and resized 1000px desktop home/list windows showed no horizontal overflow. Arrow placement, visible keyboard focus, arrow-key language selection, Tab/Enter submission, keyboard link navigation and skip-link focus were checked. The user's authorized local demo credential file was used only for local Keycloak sign-in; no credential values were printed or committed, and no live orders were modified. A screenshot is saved under ignored `artifacts/ui-review/management-desktop-sv.png`. All five focused management-page/login tests pass using `bin/DropdownVerification/`; focused formatting and `git diff --check` pass. The first sandboxed test attempt was interrupted because Docker access stalled; the successful run used local Docker access and disposable databases. Full regression and station tests were not rerun for this styling-only slice. Enlarged-text and forced-colors visual checks remain pending; do not claim those checks complete.
