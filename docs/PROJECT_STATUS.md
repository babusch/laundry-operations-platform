# Project status

Last updated: 2026-09-20

## Current phase

**Phase 0 — discovery and risk validation**

Initial application scaffolding has begun. The architecture and delivery order are documented, but real workflows, hardware, and deployment constraints still need validation.

The Windows development toolchain has been verified. Repository-level .NET, Node.js, and pnpm versions, the pnpm workspace, formatting rules, and ignore rules are now in place. The root .NET solution, local PostgreSQL Docker Compose service, and first cloud API health endpoint with an integration test are established. The provisional `scan.observed` v1 JSON contract now defines the first barcode/RFID message in the walking skeleton.

## Next outcome

Current checkpoint: the Development gateway-to-cloud path is authenticated end to end, including signing-key rollover. The gateway obtains and caches a short-lived Keycloak token, sends its immutable outbox event over local HTTPS, and the cloud validates the token and token-derived tenant/plant scope before durable ingestion. Both application routes remain loopback-only in Development; Staging and Production ingestion remain disabled.

Cloud verification includes 55 tests covering validation, authentication, signing-key rollover, local-only access, tenant/plant boundaries, concurrent duplicates/conflicts, lost-acknowledgement retries, delayed observations, and database outage/recovery. The initial migration and additive payload-preservation migration are applied to the development database. Docker PostgreSQL uses `localhost:15432` to avoid an existing Windows PostgreSQL service on port 5432. See `docs/DEVELOPMENT_SETUP.md` for repeatable commands.

The user approved PostgreSQL for plant storage ([ADR 0005](decisions/0005-use-postgresql-for-plant-storage.md)). Checkpoints 1–4 of the [local gateway plan](LOCAL_GATEWAY_PLAN.md) are implemented. `Laundry.Edge` now uses trusted `https://localhost:7200` as its primary Development address, with `http://localhost:5200` retained only as a temporary loopback transition path. Its independent `plant-postgres` storage uses port 15433 and its own named volume. Health endpoints distinguish process liveness from plant database connectivity; neither depends on cloud availability.

Gateway `POST /api/scans` validates `submit-scan.v1`, assigns gateway time, and commits immutable observation evidence plus a pending outbox row in one transaction. It is Development/loopback-only and now requires trusted HTTPS, an enrolled browser source, `scans.submit`, and antiforgery. Tenant/plant/station scope comes from the trusted-source record. The configured device authority is removed; provisional v1 `deviceId` is untrusted compatibility metadata and will not be mandatory in the replacement contract. Retries retain the original receipt; conflicting IDs return 409. See [ADRs 0006](decisions/0006-durable-local-scan-acceptance.md) and [0014](decisions/0014-authenticate-local-scan-submission.md).

The gateway now forwards stored events unchanged to the protected local cloud API in Development using a cached short-lived Keycloak token. Durable scoped leases, retry scheduling, and validated receipts update outbox state to `synchronized`; permanent event failures become `needsAttention`. Cloud or identity outages remain pending without blocking local acceptance. The acceptance, delivery-tracking, replay-audit, and trusted-source-security migrations are applied locally; startup does not mutate schemas. See [ADRs 0007](decisions/0007-forward-plant-outbox-to-local-cloud.md) and [0013](decisions/0013-gateway-acquires-short-lived-tokens.md).

Development storage retains prior synchronized synthetic observations and one additional pending observation accepted during the HTTPS proof. Docker Desktop and the plant database are running; the gateway smoke-test process was stopped afterward.

Checkpoint 4 is complete: Development/loopback-only `/api/sync` summary, bounded metadata lists/details, replay history, and idempotent audited replay. The `ReplayAudit` migration is applied to the local plant database. See [ADR 0008](decisions/0008-local-sync-diagnostics-and-audited-replay.md) and the setup guide.

Current verification: all 177 .NET tests pass (121 gateway, 55 cloud, one cross-component scenario), and all 10 Node contract tests pass with unchanged schemas. A live trusted-HTTPS proof enrolled a new synthetic browser source, durably accepted one scan, and returned the original observation on unchanged retry; the helper disclosed no credential, antiforgery token, or tag value. The build succeeds with zero warnings/errors. Tests cover authenticated and antiforgery-protected durable scan acceptance, arbitrary untrusted v1 device metadata, absent credentials and permissions, single-use/concurrent browser enrollment, secure cookie attributes, tampered credentials, HTTPS/loopback restrictions, trusted-source tenant/plant isolation, credential lifecycle/rotation overlap, immediate local disable/revocation, immutable source scope, append-only security audit, gateway token resilience, scoped diagnostics, replay behavior, storage outage/recovery, and end-to-end gateway/cloud delivery without payload changes or duplicates.

Checkpoint 5 security is proceeding in reviewed slices. ADR 0009 remains the broader proposed model; its offline policy is approved. Local Keycloak, development gateway identity, cloud validation, gateway token acquisition, identity outages, disabled identities, signing-key rollover, and protected local scan acceptance are verified (ADRs 0010–0014). Checked-in forwarding still defaults off so ordinary gateway startup requires no secret or cloud; the documented helper enables authenticated forwarding. Checkpoints 5.4.1–5.4.4 are complete: the Development station API has trusted HTTPS, an empty-by-default trusted-source registry and common identity seam, simulated browser enrollment using a short-lived single-use code and secure host-only cookie, antiforgery, explicit `scans.submit`, and trusted-source scope enforcement around the unchanged durable transaction. No physical-device table or device-based authorization was added. Authenticated operator/workflow context is explicitly required before scans can count as laundry business actions, but its design remains deferred for joint review. The next small slice is 5.4.5: prove WAN-independent source access, gateway restart persistence, immediate local revocation, invalid credential/CSRF rejection, and idempotent retry end to end. Attributed supervisor replay, production identity hosting, asymmetric installation credentials, enrollment recovery, and production credential lifetimes also remain open.

Produce an executable walking skeleton in which a simulated scan travels through the operator application and local gateway to the cloud API and becomes visible in an audit view.

Before that implementation begins, confirm:

- Initial pilot plant and its exact workflow.
- Whether stock is individually tagged, counted in bulk, measured by weight, or a mixture.
- First barcode and RFID device models.
- Required label printers and machine protocols.
- Target cloud provider and identity provider.
- Languages required on the shop floor.
- Regulatory, hygiene, retention, and data-residency constraints.

## Active architectural baseline

Latest identity verification (2026-09-13): Keycloak starts successfully, discovery/public signing keys and scoped client-credentials issuance pass over trusted HTTPS, and the gateway uses the token against the protected cloud route. Private `.env` stays ignored and untracked. Agents may let normal authorized application/Compose execution consume it internally, but may inspect values only for a specifically authorized development-credential investigation and must never publish or commit them.

Identity setup slice: the user approved product-managed identity and local Keycloak evaluation. The optional HTTPS-only development Compose stack, dedicated identity PostgreSQL volume, setup guide, and verification scripts are added (ADR 0010). Live startup and trusted HTTPS verification pass. The gateway now obtains and uses tokens, while production provider and hosting remain open.

Development gateway identity bootstrap is complete (ADR 0011): the reproducible `laundry-development` realm imported successfully, and an idempotent initializer applies the explicit gateway role scope to existing realms. Live verification proves invalid credentials are rejected and the valid service account receives a five-minute token containing only the `scans.ingest` application permission, the cloud API audience, and fixed synthetic tenant/plant claims. The user's distinct gateway credential remains in ignored private `.env`; neither it nor the token is printed or stored. This local shared-secret proof does not approve production credentials or connect application authentication.

Gateway token acquisition and initial resilience slice complete (ADR 0013): a singleton provider obtains client-credentials tokens, caches them only in memory, serializes refresh, refreshes before expiry, and retries a cloud 401 once with a replacement. A live outage test stopped Keycloak before a fresh gateway requested a token: the gateway stayed ready, accepted a synthetic scan durably, recorded `identity_connection_failed`, and synchronized the unchanged event automatically after Keycloak restarted. Token acquisition and each cloud attempt now have independent five-second bounds. Application processes were stopped afterward; the four local dependency containers remain running. No credential, token, tag value, or generated identifier was printed.

Disabled-gateway resilience is verified: with a fresh gateway holding no cached token, the exact `service-account-gateway-development` account was disabled. Local readiness and durable scan acceptance continued, token issuance returned 401, and the event remained pending with `identity_credentials_rejected`; no cloud delivery occurred. Re-enabling the account synchronized the unchanged event automatically, and the normal identity verification passed afterward. Already-issued signed tokens remain usable until their current five-minute expiry, so disabling an account is bounded revocation rather than instantaneous revocation.

Signing-key rollover is verified without replacing or exposing private keys. An automated cloud test proves that unknown-key metadata refresh accepts the new authority-published key, retains the previous overlap key, and rejects an unrelated key. A live helper temporarily added a higher-priority Keycloak RSA provider while the cloud held cached metadata, delivered synthetic observations under both keys, removed only that provider in cleanup, and confirmed the original development key was active again. The cloud application was stopped after the test; the four dependency containers remain running.

Approved station requirement (2026-09-08): stable source identity with configurable operational purpose (dedicated, flexible, or default-with-switching). Fixed station roles are optional. See DOMAIN.md and PRODUCT.md. This is documented only; the raw scan contract and application behavior are unchanged.

- Repository: one monorepo.
- Cloud: ASP.NET Core modular monolith, worker, PostgreSQL, object storage, and Redis only when justified.
- Management UI: Next.js with TypeScript.
- Shop floor: installable React/Vite PWA with IndexedDB emergency queue.
- Plant: local .NET gateway and local PostgreSQL.
- Integration: keyboard-wedge scanners may feed the PWA; fixed RFID and industrial devices connect through the gateway.
- Synchronization: append-only, versioned, idempotent events with outbox/inbox processing.
- UI design: all user-facing design and review work follows the repository `apple-design` skill together with `docs/UI_DESIGN.md`.

## Update rules

Update this file when the active phase, next outcome, significant blocker, or architectural baseline changes. Do not use it as a historical log; permanent reasoning belongs in an ADR.
