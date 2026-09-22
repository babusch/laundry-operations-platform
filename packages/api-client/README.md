# API client

This private workspace package provides the typed browser client for plant-gateway HTTP APIs.

`src/generated/station-api.ts` is generated from `apps/edge/Laundry.Edge/OpenApi/station-api.v1.yaml`. Do not edit the generated file directly. The OpenAPI document references the canonical `submit-scan.v2` JSON Schema so the request shape is not handwritten in the PWA.

Regenerate and check the client from the repository root:

```powershell
corepack pnpm generate:api-client
corepack pnpm --filter @laundry/api-client typecheck
```

The browser client uses same-origin paths by default. The gateway-hosted PWA calls the local gateway directly; Vite forwards the same `/api` and `/health` paths during visual development. Callers may still supply an explicit base URL for non-browser tests and tools.

The current client describes gateway readiness and `POST /api/scans`. Enrollment and session APIs will be added when their UI behavior is reviewed.
