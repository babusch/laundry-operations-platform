# 0009 — Identity, permissions, and offline access

Status: Proposed — awaiting user review; not implementation authority  
Date: 2026-09-08

Review update (2026-09-08): the user approved the offline-access policy below. Identity mechanisms, provider selection, and implementation details remain under review; this is not approval to implement the entire proposal.

## Context

Checkpoint 5 replaces local-development trust with authenticated communication. Existing scan durability, immutable payloads, tenant/plant isolation, and idempotent delivery must remain unchanged. An internet or cloud identity-service outage must not become a per-scan dependency. This proposal does not supersede ADRs 0004/0006/0007/0008 until reviewed and implemented in explicit small steps.

## Proposed identities and ownership

- Gateway: a unique machine identity registered to exactly one tenant and plant initially. Replacing a computer creates a new credential/enrollment; historical events retain their original identities. Do not silently reassign a gateway with queued events to another plant.
- Station/device: locally enrolled source identity, mapped to allowed station/device IDs at one plant. Fixed-reader adapters may run under gateway-managed credentials. Network address, JSON identifiers, and forwarded headers are not proof of identity.
- Human: a stable subject plus issuer, with tenant/plant memberships and explicit permissions. Human identity is separate from station and gateway identity. Machine credentials cannot authorize human administrative operations.
- The cloud and gateway each enforce permissions at their own boundary. Database access remains private. Browser code must never contain a reusable gateway machine secret.

## Proposed permissions

Here, source means the registered origin of a scan: its tenant, plant, logical station (for example, dirty receiving), and physical device or enrolled adapter. A source cannot claim another station/device merely by changing JSON fields. Authorized enrollment can change hardware assignments without changing the shared contract or hard-coding a vendor.

Station clarification approved by the user on 2026-09-08: source identity does not impose a fixed operational role. Stations may be dedicated, flexible, or default-with-switching according to configuration, applicable permissions, and hardware capabilities. Performing receiving at an alternative station retains that station's real identity. See [DOMAIN.md](../DOMAIN.md#station-identity-and-operational-purpose); workflow-context implementation remains deferred and does not change the current scan contract.

Diagnostics are synchronization health and troubleshooting information: queued/synchronized/needs-attention counts, waiting time, delivery attempts, and safe failure codes. Replay means explicitly requeuing the same stored event after a blocking issue is reviewed or corrected, preserving its event ID and payload. It is not a new physical scan or a command to repeat a machine operation. Ordinary transient delivery retries are automatic; a supervisor or authorized support person handles exceptional replays. The audit records the authenticated person, event, time, and reason for accountability.

| Principal | Permission | Scope |
|---|---|---|
| Registered gateway | `scans.ingest` at cloud | Its registered tenant/plant only |
| Enrolled station or device adapter | `scans.submit` at gateway | Its permitted station/device source |
| Authorized diagnostic viewer | `sync.read` | Explicit tenant/plant membership |
| Authorized supervisor | `sync.read`, `sync.replay` | Explicit tenant/plant membership |
| Authorized enrollment administrator | Enroll/revoke identities | Explicit managed plants; no implicit global access |

These are proposed permission names, not an implemented role hierarchy. Deny by default. Derive trusted scope from verified identity and server-owned registration, then compare it against payload scope. Test permissions on retries as well as first submissions. Replay audits must record authenticated issuer/subject; never accept an actor name from the command body or rewrite old unattributed audits.

## Proposed gateway-to-cloud mechanism

Use OAuth 2.0 client credentials for the gateway, obtaining short-lived access tokens from an established authorization server. Prefer asymmetric client authentication (a per-gateway private key with registered public key/certificate) over a shared fleet password. Cloud validates signature, issuer, intended API audience, lifetime, machine permission, and active tenant/plant registration. Tokens go in the HTTP Authorization header, never in immutable event JSON.

Client credentials are for confidential machine clients, not browser operator login ([RFC 6749 section 4.4](https://www.rfc-editor.org/rfc/rfc6749.html#section-4.4)). Asymmetric client authentication follows the recommendation in [OAuth security BCP section 2.5](https://www.rfc-editor.org/rfc/rfc9700.html#section-2.5).

Provider choice remains open between the existing architecture's Entra ID/Keycloak candidates; confirm customer identity environment and hosting constraints before installing anything. Confirm supported client authentication method, token lifetimes, signing-key rollover, and registration/revocation enforcement before 5.2. Do not implement a custom identity provider or invent token validation cryptography.

Use HTTPS with certificate verification for token and API endpoints. Do not disable certificate validation for development. Private keys and credentials stay out of Git, images, logs, scan records, and browser storage. Development may use disposable locally provisioned credentials with a documented trust setup. Production key storage, rotation overlap, lost-computer revocation, and recovery require an explicit deployment design; do not assume a portable file is adequate private-key protection.

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

Do not make human login a new requirement for the current raw-observation contract: fixed devices can record observations without an operator. Future workflows that require an operator need a separately approved offline sign-in design, allowed duration, and local verification method before rollout. Do not copy cloud passwords to the gateway, assume a cloud access token is indefinitely valid, or claim that all production workflows are offline-ready yet.

## Station transport and human login design boundaries

Station-to-gateway traffic uses local HTTPS with a stable gateway name and managed trust. Browser stations are public clients; do not use gateway-style client secrets in a PWA. Choose station enrollment/proof-of-possession and human session mechanisms after identifying managed browsers/OS and device SDK constraints. A client certificate may fit a managed native adapter but must not be assumed usable by every browser station without validation.

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
