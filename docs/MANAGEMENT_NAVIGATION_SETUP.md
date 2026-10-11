# Development management navigation

The user verified the live order list and approved one bookmarked entry point. This small bridge is server-rendered by the existing API; it introduces no dependency or new service. The long-term management UI remains Next.js in `apps/admin-web` under ADR 0019, not these Development-only pages. No handwritten TypeScript API contract or production hosting change is introduced.

## One bookmark

Stop the running API with Ctrl+C, then run from the repository root:

```powershell
.\deploy\local\Start-DevelopmentManagement.ps1 -WithOrders
```

Open `https://localhost:7100/management` and bookmark it. No migration/seed is needed. The page offers sign-in/status, order navigation, a readable paginated list, order details, and CSRF-protected local sign-out. Creating/editing orders and receiving/dispatch are not UI actions yet. Existing JSON routes remain available for diagnostics; users no longer need them for ordinary viewing.

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

Tests cover signed-out/expired navigation, signed-in list/detail rendering, both languages, HTML encoding, wrong-plant rejection, real form antiforgery sign-out and fixed login return destinations. Existing API authentication/storage tests remain relevant.

Verification (2026-10-11): `dotnet test LaundryOperations.sln --no-restore -p:OutputPath=bin/ManagementPageVerification/` passes all 342 tests (208 cloud, 133 gateway, one cross-component). After final markup/color refinements, all five focused page/login cases pass using `ManagementPageFinalVerification` outputs. Focused formatting and `git diff --check` pass. Station/TypeScript tests were not rerun: no station code, generated clients or frontend dependencies changed. Test preview fixtures are generated only under ignored test build outputs and contain synthetic data. The live API was not restarted and no private environment file was inspected.

Visual verification is pending: the browser tool could not reach the temporary synthetic-preview HTTP server, and local file preview was blocked by browser policy. No bypass was attempted; preview servers were stopped. Per the design skills, do not claim desktop/mobile, text zoom, keyboard focus or high-contrast visual completion until the live page is reviewed in the user's browser. The page is implemented/tested, but design review remains open.
