# API client

This private workspace package provides the typed browser client for plant-gateway HTTP APIs.

`OrderDownloadPaths`, `OrderDownloadComponents` and `OrderDownloadOperations` are separately generated from `order-download.v1.yaml` for the opt-in cloud gateway download endpoint. This is bearer-authenticated with `orders.download`, not a management-cookie or station-source route. Keep the opaque `nextCursor` even when `hasMore` is false; a future gateway receiver must commit it atomically with applying copies. Types do not implement downloading, storage or synchronization acknowledgements. The OpenAPI fields/hidden-note rule reference the canonical published-order JSON Schema. Regenerate just these types with `pnpm --filter @laundry/api-client generate:order-download`.

The separate management `createOrderPlanningClient` and `src/generated/orders-api.ts` describe opt-in planned-order creation, details, paginated unfiltered lists and planned updates from `apps/cloud/Laundry.Api/Orders/OpenApi/orders.v1.yaml`. Its browser client defaults to same-origin credentials; mutations require a fresh management CSRF token and stable idempotency key explicitly. Updates also require current `orders.plan.edit`, all five fields (null clears optional text), and the exact latest strong ETag in `If-Match`. Keep the same body/key/precondition for uncertain-result retries; fetch current details after replay rather than treating a historical receipt as the latest state. Reads require current `orders.read` but neither token nor key. Follow `nextCursor` with the same limit. Do not reuse station-source identity or the gateway client to manage orders. No management UI is implied by this client.

`src/generated/station-api.ts` is generated from `apps/edge/Laundry.Edge/OpenApi/station-api.v1.yaml`. Do not edit the generated file directly. The OpenAPI document references the canonical `submit-scan.v2` JSON Schema so the request shape is not handwritten in the PWA.

The order client also describes `GET /api/v1/plants/{plantId}/orders/{orderId}/history`: current `orders.read`, ascending version pagination (default 25/max 100), order/scoped/limit-bound opaque cursor and no mutation headers. Creation includes `initialPlan`; changes include only changed fields. `actorDisplayName` is nullable verified display metadata; never substitute internal issuer/subject or invent a name. Reads have no writes and customer deactivation does not hide history. No history UI is implemented by this package.

Order requests now accept optional boolean `notesVisibleToOperators`: omit on create to copy the plant default, omit on edit to preserve the stored choice, or explicitly send true/false. Null is invalid. Current order reads/new receipts include the choice; immutable old retry responses can omit it, meaning unknown in that receipt. Fetch current details instead of treating absence as false. History uses a separate `OrderHistoryPlan` with nullable recorded visibility; changed-field visibility is a literal `true`/`false` string or null for missing legacy evidence.

`GET /api/v1/plants/{plantId}/orders/settings/notes` reads the default with `orders.read`. Its `PUT` requires separate `orders.notes.default.edit`, antiforgery, retained `Idempotency-Key`, and exact strong scoped `If-Match` from GET. An unconfigured default is hidden/version 0. Default changes affect only future creations. Keep the exact command on uncertain retries; old accepted results are not today's default. No permissions are granted by using this client.

Regenerate and check the client from the repository root:

```powershell
corepack pnpm generate:api-client
corepack pnpm --filter @laundry/api-client typecheck
```

The browser client uses same-origin paths by default. The gateway-hosted PWA calls the local gateway directly; Vite forwards the same `/api` and `/health` paths during visual development. Callers may still supply an explicit base URL for non-browser tests and tools.

The current client describes gateway readiness, the Development enrollment creation/exchange flow, `GET /api/source-session`, and `POST /api/scans`. The station keeps the short-lived enrollment code only inside one setup request chain and does not display it or the returned internal identifiers. The secure browser credential is an `HttpOnly` cookie and therefore cannot be read by the PWA. Before each scan mutation, the PWA obtains a fresh session antiforgery token and sends it in the server-named header.
