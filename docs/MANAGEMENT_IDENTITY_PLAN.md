# Management identity: small implementation steps

Date: 2026-10-11.
Status: initial permissions and session hosting approved; isolated authorization rules and disabled-by-default login backend implemented. Real Keycloak/browser login awaits local setup.

## Approved initial access

Use the existing local Keycloak development setup for a normal development management account, separate from the Keycloak administrator and gateway service account. The management account may view orders, create planned orders, and edit planned orders only within its explicitly assigned tenant (company) and plant. It receives no implicit platform, identity-administration, scan-ingestion, replay, cancellation, completion, or other permissions.

The user approved this initial permission set on 2026-10-11. This does not approve a production identity deployment or a complete role hierarchy. Operator sign-in and offline shop-floor authorization remain separate future work.

## Step 1: permission rules (implemented)

- The Orders module owns `orders.read`, `orders.create`, and `orders.plan.edit` in `OrderPlanningPermissions`. The management bundle includes exactly those permissions. Each application boundary still requires the permission for the particular operation.
- `ManagementOrderAuthorization` accepts a trusted actor and application-owned plant grants, then produces an `OrderPlanningScope` only after authorization succeeds.
- The actor must be authenticated and classified as human by a future verified authentication adapter. Unknown actors, gateways, and trusted station sources are denied, even with otherwise matching grants.
- Stable actor identity is the exact issuer plus subject, not email, display name, or a role label. Requests must not supply their own actor classification, authentication flag, grants, or authoritative company/plant scope.
- An active grant must match that actor, requested tenant, requested plant, and exact known permission together. Access to one plant cannot supply the missing permissions for another plant. Empty scope/identity, absent grants, inactive grants, unknown permissions, and wildcard permissions deny access.
- Permission names compare case-sensitively. Re-evaluating against inactive/removed grants denies access; the rule does not cache a previous authorization result. This is not a promise of instantaneous identity-provider revocation: grant loading and session/token lifetime policy remain unimplemented.
- These are pure application authorization rules, not token validation. A `ManagementActor` object is not itself authentication evidence. No claims mapper, production grant reader, identity persistence, `IOrderPlanningAccess` adapter, login route, or order endpoint is enabled by this slice. Existing gateway authentication is unchanged.
- Tests use synthetic identities and grants. They prove policy decisions, not real Keycloak login or machine-token rejection at a future HTTP endpoint.

Verification: all 21 new authorization tests pass. The full .NET suite passes with 238 tests (104 cloud, 133 gateway, one cross-component). No UI tests were run because this slice changes no UI. Database regression tests use disposable PostgreSQL containers, not the development databases.

## Step 2: connect management sign-in (backend implemented; live setup pending)

The user approved [ADR 0019](decisions/0019-management-server-owned-login-session.md): .NET owns the OpenID Connect login and protected management cookie, with the future Next.js UI and backend paths sharing one management HTTPS origin. The first proof runs at the loopback cloud origin without a management UI or order writes. Named authentication schemes, safe session status, and CSRF-protected local sign-out are implemented; gateway bearer authentication remains separate. Sessions last 15 minutes without sliding refresh and retain no provider tokens. A public client import file and hidden-prompt startup helper are added, but no actual Keycloak client/user or application memberships have been created. Follow [Management sign-in setup](MANAGEMENT_SIGN_IN_SETUP.md) for the user-guided live check.

Use the separate management application client in the existing application realm, not the administrative master realm or gateway client. The backend constructs a minimal protected identity with validated issuer/subject and server-owned interactive-login provenance. Production hosting, session/key lifecycle, broader sign-out, and revocation are not decided by this Development proof.

Validate the authentication boundary before constructing the trusted actor: signature/session integrity, issuer, intended audience/client, expiry, and human-login provenance. A gateway client-credentials token must not become a human merely because it contains a subject or a permission name. Never accept the JSON actor/grant records above as authentication inputs.

Provision a synthetic development account with a privately chosen password, explicit scoped grants, and no Keycloak-administrator privileges. Do not store a real password in committed fixtures or reuse a machine secret. Public configuration and repeatable provisioning can be committed; credentials remain private. Make setup instructions clear about where passwords are entered and saved.

Verify real login, sign-out, unauthenticated access, wrong scope, insufficient permission, expired/invalid sessions or tokens, and gateway/source credential rejection. Protect cookie-backed writes against CSRF. Do not expose order mutations until customer eligibility and server-owned memberships are connected.

## Step 3: customer/plant eligibility and order routes (later)

Introduce the smallest real plant/customer configuration and application-owned membership/grant storage. Implement `IOrderPlanningAccess` using the verified current human and current scoped grants, and check customer eligibility through its owning module. Re-evaluate access for first submissions and retries. These prerequisites must not be replaced with a permissive development fallback.

Only then enable the reviewed order API and management UI. Cloud-to-plant publication and operational order authority remain separate slices, as described in [Order planning contract](ORDER_PLANNING_CONTRACT.md).

## Boundaries

No new package, service, datastore, credentials, identity-provider configuration, database migration, or UI was added for Step 1. It requires no running Keycloak or Docker for its focused tests:

```powershell
dotnet test apps/cloud/Laundry.Api.Tests --filter "FullyQualifiedName~ManagementOrderAuthorizationTests"
```

This document complements [ADR 0009](decisions/0009-identity-permissions-and-offline-access.md), [local identity setup](LOCAL_IDENTITY_SETUP.md), and the [order contract](ORDER_PLANNING_CONTRACT.md).
