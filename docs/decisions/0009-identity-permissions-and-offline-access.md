# 0009 — Identity, permissions, and offline access

Status: Proposed — awaiting user review; not implementation authority  
Date: 2026-09-08

Review update (2026-09-08): the user approved the offline-access policy below. Identity mechanisms, provider selection, and implementation details remain under review; this is not approval to implement the entire proposal.

Source-identity review update (2026-09-20): the user approved the refined source direction. Use a separately enrolled source identity in addition to any operator session. An opaque secure cookie is the default for an ordinary browser installation; a managed workstation certificate may replace it where certificate deployment is available. Remote managed adapters prefer unique client certificates, while in-process or co-located adapters use the gateway or operating-system trust boundary. Bind the trusted source to tenant, plant, and station, not to one mandatory physical device. A station may use several inputs; optional equipment inventory remains separate and is added only for a validated hardware-management need. Exact operator sign-in, active workflow, and deployment choices remain under review.

Implementation update (2026-09-09): ADRs 0010–0012 establish local Keycloak, a development gateway identity, and cloud-side JWT validation. The gateway does not yet acquire tokens, and production provider/credentials remain undecided.

## Context

Checkpoint 5 replaces local-development trust with authenticated communication. Existing scan durability, immutable payloads, tenant/plant isolation, and idempotent delivery must remain unchanged. An internet or cloud identity-service outage must not become a per-scan dependency. This proposal does not supersede ADRs 0004/0006/0007/0008 until reviewed and implemented in explicit small steps.

## Proposed identities and ownership

- Gateway: a unique machine identity registered to exactly one tenant and plant initially. Replacing a computer creates a new credential/enrollment; historical events retain their original identities. Do not silently reassign a gateway with queued events to another plant.
- Station/source: locally enrolled browser installation or adapter, mapped to one station at one plant. Fixed-reader adapters may run under gateway-managed credentials. Network address, JSON identifiers, optional physical-equipment claims, and forwarded headers are not proof of identity.
- Human: a stable subject plus issuer, with tenant/plant memberships and explicit permissions. Human identity is separate from station and gateway identity. Machine credentials cannot authorize human administrative operations.
- The cloud and gateway each enforce permissions at their own boundary. Database access remains private. Browser code must never contain a reusable gateway machine secret.

## Proposed permissions

Here, source means the enrolled browser installation or adapter that originates a request: it is assigned to one tenant, plant, and logical station (for example, dirty receiving). A source cannot claim another station merely by changing JSON fields. A station may use several physical inputs; those are not automatically authenticated equipment identities.

Station clarification approved by the user on 2026-09-08: source identity does not impose a fixed operational role. Stations may be dedicated, flexible, or default-with-switching according to configuration, applicable permissions, and hardware capabilities. Performing receiving at an alternative station retains that station's real identity. See [DOMAIN.md](../DOMAIN.md#station-identity-and-operational-purpose); workflow-context implementation remains deferred and does not change the current scan contract.

Diagnostics are synchronization health and troubleshooting information: queued/synchronized/needs-attention counts, waiting time, delivery attempts, and safe failure codes. Replay means explicitly requeuing the same stored event after a blocking issue is reviewed or corrected, preserving its event ID and payload. It is not a new physical scan or a command to repeat a machine operation. Ordinary transient delivery retries are automatic; a supervisor or authorized support person handles exceptional replays. The audit records the authenticated person, event, time, and reason for accountability.

| Principal | Permission | Scope |
|---|---|---|
| Registered gateway | `scans.ingest` at cloud | Its registered tenant/plant only |
| Enrolled browser installation or adapter | `scans.submit` at gateway | Its assigned tenant, plant, and station |
| Authorized diagnostic viewer | `sync.read` | Explicit tenant/plant membership |
| Authorized supervisor | `sync.read`, `sync.replay` | Explicit tenant/plant membership |
| Authorized enrollment administrator | Enroll/revoke identities | Explicit managed plants; no implicit global access |

These are proposed permission names, not an implemented role hierarchy. Deny by default. Derive trusted scope from verified identity and server-owned registration, then compare it against payload scope. Test permissions on retries as well as first submissions. Replay audits must record authenticated issuer/subject; never accept an actor name from the command body or rewrite old unattributed audits.

## Proposed gateway-to-cloud mechanism

Use OAuth 2.0 client credentials for the gateway, obtaining short-lived access tokens from an established authorization server. Prefer asymmetric client authentication (a per-gateway private key with registered public key/certificate) over a shared fleet password. Cloud validates signature, issuer, intended API audience, lifetime, machine permission, and active tenant/plant registration. Tokens go in the HTTP Authorization header, never in immutable event JSON.

Client credentials are for confidential machine clients, not browser operator login ([RFC 6749 section 4.4](https://www.rfc-editor.org/rfc/rfc6749.html#section-4.4)). Asymmetric client authentication follows the recommendation in [OAuth security BCP section 2.5](https://www.rfc-editor.org/rfc/rfc9700.html#section-2.5).

Provider choice remains open between the existing architecture's Entra ID/Keycloak candidates; confirm customer identity environment and hosting constraints before installing anything. Confirm supported client authentication method, token lifetimes, signing-key rollover, and registration/revocation enforcement before 5.2. Do not implement a custom identity provider or invent token validation cryptography.

Use HTTPS with certificate verification for token and API endpoints. Do not disable certificate validation for development. Private keys and reusable gateway/adapter credentials stay out of Git, images, logs, scan records, and JavaScript-accessible browser storage. An enrolled browser station may use a gateway-issued `Secure`, `HttpOnly`, host-only cookie that JavaScript cannot read; it identifies the browser profile, not hardware or a person. Development may use disposable locally provisioned credentials with a documented trust setup. Production key storage, rotation overlap, lost-computer revocation, and recovery require an explicit deployment design; do not assume a portable file is adequate private-key protection.

On token expiry, acquire a replacement independently of local acceptance. Bound refresh attempts and back off to prevent loops. Cloud identity-service failures pause forwarding, not local scanning. Keep events pending with a connection-level authentication diagnostic. Distinguish invalid credentials from an event-specific scope rejection; do not bulk-quarantine otherwise valid scans on a token acquisition failure. Never mark a scan synchronized without the existing validated receipt. Revocation must stop new authorized cloud ingestion according to a specified/tested enforcement interval; short-lived tokens alone do not promise instant revocation.

## Approved offline policy for the initial secure slice (not yet implemented)

| Situation | Approved behavior |
|---|---|
| Internet, cloud API, or cloud identity provider is unavailable | An already enrolled source with locally valid credentials can continue raw scan acceptance. Outbox forwarding waits. |
| Station is unknown, locally revoked, or cannot prove identity | Reject normal gateway acceptance. Do not silently invent trusted attribution. |
| Cloud credentials expire during an outage | Preserve queued events, continue independently authorized local scans, and obtain new cloud credentials after recovery. |
| New station enrollment, role grant, or privilege escalation while offline | Do not allow it in the initial secure slice. |
| Supervisor replay while cloud identity verification is unavailable | Initially require online reauthentication; retain needs-attention evidence for later review. Ordinary scanning continues. |
| Cloud revokes a station while its gateway is disconnected | Gateway cannot know immediately. Local revocation remains possible; central revocation takes effect after the gateway learns it. Explicitly accept and document this limitation before pilot use. |
| Gateway/local database fails | No plant acceptance acknowledgement. PWA emergency storage remains separate future work. |

This policy has no artificial internet-outage timer on otherwise valid enrolled machine scanning. It does not bypass credential expiry, local revocation, or required human workflow authorization. Local credential validity/renewal must support the agreed multi-day outage requirement without needing cloud calls; choose exact lifetimes and maintenance procedures with the plant deployment model. This remains a pilot-readiness requirement, not a solved detail.

The current raw-observation contract does not yet represent operator or workflow context. A fixed reader may technically emit raw signals without an operator, but those signals must not be counted as receiving, packing, dispatch, inventory, or another operational action without the separately authorized human/workflow context required by that future workflow. Exact operator sign-in, active-session, confirmation, offline duration, and local verification behavior remain deferred. Do not copy cloud passwords to the gateway, assume a cloud access token is indefinitely valid, or claim that all production workflows are offline-ready yet.

## Station transport and human login design boundaries

Station-to-gateway traffic uses local HTTPS with a stable gateway name and managed trust. Browser stations are public clients; do not use gateway-style client secrets in a PWA. Use a separately enrolled source identity: an opaque secure cookie is the broadly compatible browser default, while a managed workstation certificate may replace it when the installation can provision and maintain certificates. A remote managed adapter prefers its own client certificate; a co-located adapter may use operating-system-protected IPC. Keep these mechanisms behind a common source-identity boundary. Operator identity remains separate.

For online human login, use OpenID Connect through the chosen identity provider, with a standard authorization-code/PKCE flow or a server-managed session architecture as appropriate to the future frontend. Protect cookie-backed mutations against CSRF and do not confuse CORS with authentication. No login UI or framework/provider installation is authorized by this document.

## Delivery sequence and acceptance criteria

1. 5.1: Review this model with the user, especially offline administrative restrictions and machine/human separation. Resolve provider/deployment questions before writing provider-specific code.
2. 5.2: One registered gateway securely submits to one test cloud tenant/plant over HTTPS; missing/invalid credentials and wrong tenant/plant fail, including retries. Preserve existing development restrictions until the replacement is verified; no unauthenticated staging fallback.
3. 5.3: Test token expiry, key rotation, disabled gateway, identity-provider outage/recovery, lost acknowledgements, and preserved outbox evidence. Define how administrators resolve revoked credential failures without changing event IDs.
4. 5.4: Enroll a test station and prove local HTTPS/source authorization and offline continuity. Choose certificate/trust provisioning before connecting real machines.
5. 5.5: Protect diagnostics and replay with human permissions; require attributable, atomic audits and replay idempotency. Preserve previous audit evidence and reject machine-only replay callers.
6. Verify the complete path before any remote pilot deployment. No full user-management UI, billing authorization, production rollout, or infrastructure purchase is implied.

## Open decisions

Update: the user approved product-managed identity and local Keycloak evaluation; [ADR 0010](0010-local-keycloak-development.md) authorizes that setup only. Production provider/hosting and gateway integration details remain open.

- Offline policy approved by the user on 2026-09-08; exact deployment lifetimes and procedures below remain unresolved.
- Identity provider and ownership: existing organization-managed identity or product-managed identity?
- Pilot gateway/station OS, managed browser constraints, and certificate/key deployment ownership.
- Required disconnected duration, machine credential renewal strategy, human offline sign-in needs, and acceptable central revocation delay.
- Exact token/credential lifetimes and recovery procedure, established before the relevant implementation rather than guessed here.

## Consequences and revisit triggers

Machine identity is separate from human audit attribution; a cloud authentication outage cannot discard local evidence. Operating trusted offline sources necessarily permits delayed knowledge of central revocation. If workflow or risk requirements demand immediate revocation, revisit the offline availability promise explicitly instead of silently violating it.

Implementation update: ADRs 0010–0014 now prove local Keycloak, a scoped Development gateway identity, cloud token validation, gateway acquisition/caching of short-lived tokens, and source-authenticated durable local scan acceptance. Checkpoints 5.4.1–5.4.4 add trusted Development gateway HTTPS, the empty-by-default local trusted-source security schema and project-owned identity seam, Development-only browser enrollment with atomic single-use code redemption and a secure host-only cookie, antiforgery validation, and `scans.submit` enforcement. No equipment table or configured device authorization remains. The provisional v1 device field is untrusted compatibility metadata pending a versioned replacement. Authenticated operator/workflow enforcement, attributed supervisor replay, enrollment recovery, production identity hosting, and production credential lifecycle remain later slices.
