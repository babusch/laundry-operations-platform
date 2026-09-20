# Station-to-gateway identity and access plan

Date: 2026-09-14  
Status: Source-identity direction and refined device boundary approved 2026-09-20; operator authentication, workflow behavior, and deployment details remain under review

## Goal

Allow an enrolled browser station or managed device adapter to submit scans to its plant gateway over trusted local HTTPS. The gateway must establish the real source independently of request JSON, continue validating enrolled sources without internet or Keycloak, and preserve the existing durable acceptance and retry behavior.

This is checkpoint 5.4 from [ADR 0009](decisions/0009-identity-permissions-and-offline-access.md). Human login and authorization for diagnostics/replay remain the separate checkpoint 5.5.

## Recommendation

Use one source-authorization model with different authenticators for different client types. A secure enrollment cookie is the broadly compatible browser default, not a mandatory mechanism for every installation:

| Client | Recommended credential | Reason |
|---|---|---|
| Ordinary PWA/browser station | Opaque gateway-issued credential in a `Secure`, `HttpOnly`, `SameSite=Strict` cookie | Browser code cannot safely contain a shared machine secret; same-origin cookies work naturally with a PWA served by the gateway |
| Centrally managed browser station with certificate deployment | Unique station client certificate may replace the station cookie | Stronger proof of possession is available when the customer can securely provision, select, renew, and revoke certificates |
| Remote fixed reader or vendor adapter process | Per-adapter client certificate on a dedicated mutual-TLS listener | A managed process can protect a private key and prove possession during TLS |
| Separate adapter process on the gateway computer | Operating-system-protected local IPC and service identity | Avoids unnecessary network credentials while retaining a controlled process boundary |
| Adapter running inside the gateway service | Gateway-managed internal principal | It is already inside the trusted process boundary and must not invent identity through JSON |

All authenticators produce the same internal `SourceIdentity`: source ID, credential ID, tenant ID, plant ID, station ID, principal type, permission set, and configuration version. The scan module depends on that project-owned result, not on cookies, certificates, Keycloak types, physical-device claims, or vendor SDKs.

Start implementation with one simulated browser station using the default cookie mechanism. Keep station authentication replaceable so a managed installation can use a workstation certificate without changing scan-domain rules. Add the adapter certificate authenticator only after confirming the first fixed-reader operating system, SDK process model, and certificate deployment capability. This keeps the boundary extensible without speculatively integrating hardware.

Every operational request ultimately needs both trustworthy source identity and separately authorized human/workflow context. Operator login does not identify the station, and a station cookie or certificate does not identify a person. The exact operator sign-in, offline session, active scanning-session, and confirmation behavior is deliberately deferred for later discussion.

## Important identity separation

- A **station identity** answers where the request originated.
- A **trusted-source identity** identifies the enrolled browser installation or adapter that submitted the request.
- An **equipment identity** may identify a physical reader, scale, or machine when the integration can actually establish it; it is optional and separate from source authentication.
- A **human identity** answers who performed or authorized work; its mechanism is not introduced by this slice and cannot be replaced by a source credential.
- An **operation** says what the user intends to do. It remains configurable and is not implied by station identity.

For a keyboard-wedge scanner, the browser proves only the enrolled browser installation. Keystrokes do not reliably identify which physical scanner produced them. A networked fixed-reader adapter can have its own independently enrolled credential, but that proves the adapter rather than every downstream reader unless the integration establishes those identities separately.

A station may use barcode, RFID, scales, and other inputs concurrently. Source authentication therefore binds an enrolled source to tenant, plant, and station—not to one mandatory physical device. Do not add a general equipment table in checkpoint 5.4.2. Add optional equipment inventory later only for a concrete configuration, health, maintenance, calibration, movement, or independently verified identity requirement.

An operator may use several stations, and several operators may use the same station across shifts. Therefore the station credential and operator session remain logically separate and have separate enrollment, sign-out, rotation, and revocation lifecycles. If a managed station certificate identifies the workstation, no additional station cookie is required; the operator session still remains separate.

## Network and hosting model

The gateway should serve the operator PWA and station API from the same HTTPS origin. This avoids distributing OAuth tokens to browser JavaScript, reduces CORS complexity, and lets the PWA remain installable. The plant installation needs:

- A stable gateway DNS name, not a changing IP address embedded in the PWA.
- A server certificate trusted by managed station computers.
- No certificate-warning bypass or disabled certificate validation.
- No ordinary scan submission over HTTP.
- LAN/firewall exposure only after authenticated HTTPS passes on loopback Development.

Development can begin with the trusted localhost ASP.NET certificate. Pilot certificate issuance and trust installation must be chosen after the station OS and device-management model are known. A private plant/company CA, managed internal PKI, or a certificate for an organization-controlled DNS name are candidates; this plan does not select one prematurely.

Client-certificate authentication is negotiated at the TLS connection, not per request path. If remote adapters use mutual TLS, give them a separate hostname or port that requires a certificate instead of trying to turn certificate negotiation on for selected paths of the browser listener.

## Browser-station enrollment flow

1. An authorized administrator selects an existing tenant, plant, and station and creates a short-lived, single-use enrollment code for one browser installation.
2. The station opens the PWA from the gateway's trusted HTTPS address and enters or scans that code.
3. The gateway consumes the code atomically and creates a high-entropy random station credential.
4. The gateway stores only a digest of the secret and returns the credential in a host-only `Secure`, `HttpOnly`, `SameSite=Strict` cookie.
5. Subsequent API requests resolve that credential against the local plant database on every request. Disabled, revoked, unknown, or expired credentials do not authenticate.
6. Unsafe cookie-authenticated requests also require ASP.NET Core antiforgery validation. CORS, JSON content type, or `SameSite` alone are not treated as complete CSRF protection.

The cookie authenticates the browser installation, not a person. Human sign-in can later add a separate user principal/session without changing the trusted-source record.

Because checkpoint 5.5 human administration is not built yet, the first Development proof may use an explicit loopback-only command to create one enrollment code. That bootstrap must be unavailable outside Development and must not become a production administrative bypass.

Development bootstrap audits use an explicit system actor such as `local-development-bootstrap`; they must not pretend a person was authenticated. Non-Development enrollment remains disabled until checkpoint 5.5 supplies an authorized, attributable administrator.

An opaque browser cookie remains a bearer credential: copying a complete browser profile may copy it. `HttpOnly` reduces exposure to JavaScript but does not provide hardware-bound proof. Before pilot deployment, decide whether managed-browser controls are sufficient or whether the selected station fleet supports a stronger device-bound mechanism such as managed client certificates. Do not invent a custom browser signing protocol.

### Managed-browser certificate option

Where a customer centrally manages station computers and can deploy certificates and browser policy, a unique workstation client certificate may replace the station cookie. This is stronger proof of possession but requires certificate issuance, protected private-key storage, automatic browser selection, renewal, revocation, and recovery. It is an optional deployment profile rather than a prerequisite for every laundry.

URLs, IP addresses, MAC addresses, request JSON, or an operator-selected station name are not station authentication. They may assist configuration or network controls but cannot establish trusted source identity.

## Fixed-adapter enrollment flow

When a real adapter on another computer or LAN-connected host is selected:

1. Generate or provision a unique keypair for that adapter; never share one fleet-wide private key.
2. Associate its certificate/public key with exactly one trusted source.
3. Connect to a dedicated gateway HTTPS listener that requests and validates client certificates.
4. Validate certificate chain or explicit local trust, intended client-authentication use, validity period, and the active local trusted source.
5. Resolve the certificate to the same `SourceIdentity` used by browser stations.

Private keys stay in the adapter's operating-system or hardware-backed key store where available. The database stores public certificate material or a stable public-key fingerprint, never the private key. Local trusted-source status provides immediate plant revocation without requiring an online certificate-revocation service.

An adapter running inside the gateway process uses a gateway-managed internal principal. A separate adapter process on the same gateway computer should prefer operating-system-protected IPC and service identity when the target OS supports it. Mutual TLS is primarily for a managed adapter crossing a machine or network boundary, not a requirement for every scanner process.

## Authorization of a scan

Authentication and source authorization happen before the scan body reaches durable acceptance:

1. Require an authenticated source with `scans.submit`.
2. Load its active trusted-source record from plant PostgreSQL.
3. Derive tenant, plant, and station from that trusted-source record.
4. Validate the existing `submit-scan.v1` body.
5. Compare the v1 body tenant, plant, and station IDs with the trusted source. A mismatch returns 403 and stores nothing. Do not treat the origin-supplied v1 `deviceId` as authentication proof.
6. Run the existing transaction that stores immutable evidence and its outbox row before returning success.

The body remains evidence supplied by the origin, but it does not grant authority. The protected endpoint retains the provisional v1 `deviceId` only for compatibility and does not use it for authorization. The approved replacement direction is a versioned request/event design with server-derived trusted-source attribution and optional equipment/capture-channel attribution where appropriate; device identity will not be mandatory.

Station authentication does not lock a station to receiving, dispatch, one input technology, or another operation. Operational choices and capabilities remain separate configuration. A fallback station keeps its own real trusted-source/station identity while performing an operation it is allowed and equipped to perform.

Machine credentials cannot call synchronization diagnostics, request replay, enroll another source, or act as a human. Those endpoints require their own policies.

## Local data model

Add explicit migrations for a small local source-security model:

- `trusted_sources`: stable source ID, tenant/plant/station mapping, source kind, status, and configuration version.
- `source_credentials`: credential ID, source ID, credential kind, secret digest or public-certificate identity, issued/expiry times, status, rotation overlap, and revocation time.
- `source_permissions`: explicit machine permissions; initially only `scans.submit`.
- `source_enrollment_codes`: digest, intended source, expiry, redemption time, and single-use state.
- `source_security_audit`: append-only enrollment, credential rotation, local revoke/restore, and failed administrative actions, with an authenticated actor or an explicit Development system actor.

One source can have overlapping credentials during rotation. Reassigning a credential to another tenant, plant, or station is forbidden; create a new trusted source and credential instead. Historical scan events retain their original evidence.

Do not store raw browser credentials, enrollment codes, private keys, scan identifiers, or human-entered free text in these tables or logs.

## Offline and revocation behavior

Ordinary station authentication uses the gateway and plant database only:

| Situation | Behavior |
|---|---|
| Internet, Keycloak, or cloud API unavailable | An active locally enrolled source continues submitting scans; cloud forwarding waits independently |
| Gateway available but source unknown, revoked, or invalid | Reject the request and store no scan |
| Plant database unavailable | Do not authenticate or acknowledge plant acceptance |
| Gateway restarts | Registrations and credential digests survive in PostgreSQL; valid stations reconnect without cloud access |
| Source is revoked locally | New submissions stop immediately at that gateway |
| Source is revoked centrally while gateway is offline | The gateway learns later through configuration synchronization; this unavoidable delay remains explicit |
| PWA cannot reach the gateway | Future IndexedDB emergency queue records a distinct device-local state; it is not yet plant acceptance |

The exact credential lifetime must exceed the agreed disconnected-operation requirement and be renewable locally without contacting Keycloak. It must still have an explicit maintenance, rotation, lost-device, and recovery policy before pilot use. We should choose the numbers from real plant requirements rather than guess them now.

## Proposed implementation order

Each checkpoint must leave the current repository runnable and retain the loopback boundary until its replacement has passed.

### 5.4.1 — Trusted gateway HTTPS — implemented and live-verified

- The Development gateway now makes trusted `https://localhost:7200` its primary listener.
- The existing `http://localhost:5200` endpoint remains loopback-only during transition and is not exposed to the LAN.
- A configuration test fixes HTTPS as the first address and requires every Development address to use `localhost`. A live Windows check succeeded without bypassing certificate validation, reached the plant database, accepted a synthetic scan durably over HTTPS, and found only `127.0.0.1`/`::1` listeners.
- The temporary HTTP listener remains only for non-station transition compatibility. Protected scan submission rejects it; removing the listener entirely remains a later cleanup after all local callers use HTTPS.
- The eventual stable plant name, certificate authority, provisioning, renewal, and recovery process remain open deployment decisions.

### 5.4.2 — Source registry and authentication seam — implemented and verified

- The additive `TrustedSourceSecurityFoundation` migration creates trusted sources, credentials, explicit permissions, enrollment codes, and append-only security audit. It adds no general equipment/device table and seeds no source or credential.
- The project-owned `SourceIdentity`, `ISourceAuthenticator`, and `ISourceIdentityResolver` keep cookie, certificate, operating-system, and internal authenticators behind one boundary. `SourceIdentity` contains source, credential, tenant, plant, station, principal type, configuration version, and permissions—never a mandatory physical-device ID.
- The resolver accepts only a credential that a future authenticator has already verified, requires active and currently valid credential/source records, and restricts resolution to the gateway's tenant and plant.
- Application persistence rejects tenant/plant/station/kind reassignment, immutable credential-verifier changes, and modification or deletion of source-security audit records. Rotation creates another credential and can retain a bounded active overlap.
- Tests prove tenant/plant isolation, explicit permission resolution, credential lifecycle and overlap, immediate local disable/revocation, immutable source scope, and append-only audit. Browser credential issuance and scan authorization intentionally remain later checkpoints.

### 5.4.3 — One simulated browser enrollment — implemented and live-verified

- A Development-only bootstrap creates a pending browser trusted source for the configured tenant, plant, and station, grants only `scans.submit`, and returns a ten-minute enrollment code. It requires loopback HTTPS and records explicit `local-development-bootstrap` audit actions.
- The HTTPS exchange stores only the code and credential digests, consumes the code atomically, activates the source, and returns the credential in a host-only `Secure`, `HttpOnly`, `SameSite=Strict` cookie. No `Domain` attribute is set.
- An authenticated session endpoint issues the separate antiforgery request token. A Development verification mutation proves both the source cookie and matching antiforgery token are required; checkpoint 5.4.4 now applies the same validation to scan submission.
- Tests cover missing, malformed, unknown, expired, reused, and concurrently redeemed enrollment codes; insecure HTTP; non-loopback bootstrap; tampered cookies; cookie attributes; explicit permission; and missing/valid antiforgery tokens.
- The non-disclosing live helper completed the full trusted-HTTPS flow against plant PostgreSQL without printing the code, cookie, antiforgery token, or generated identifiers. The ten-minute code and 30-day credential lifetimes are Development proof values, not production policy.
- A lost exchange response cannot reproduce the generated cookie because raw credentials are never stored. Before pilot use, authorized recovery must issue replacement enrollment and revoke the inaccessible credential; the Development proof can create a new synthetic source.

### 5.4.4 — Protect durable scan acceptance — implemented and verified

- Development gateway `POST /api/scans` now requires loopback HTTPS, an active enrolled browser credential, `scans.submit`, and a valid antiforgery token before it reads or accepts the scan body.
- Tenant, plant, and station come from the local trusted-source record and must match the provisional v1 body. The configured synthetic device ID was removed from the authorization boundary and configuration.
- The v1 `deviceId` is preserved only as untrusted compatibility metadata. It is not necessary source identity, does not restrict a station to one scanner, and will stop being mandatory in a future versioned contract.
- Validation, size limits, the atomic observation/outbox transaction, unchanged retries, conflicts, receipts, and forwarded payloads remain intact. Authentication database outages return 503 and store nothing.
- Tests prove authenticated barcode/RFID acceptance, idempotency, concurrency, mismatched scope, arbitrary v1 device metadata, absent credentials, missing permission, missing antiforgery, insecure transport, database outage/recovery, transaction rollback, and the Development/loopback boundary.
- No unauthenticated fallback exists in Staging or Production. Source authentication still does not identify the operator: authenticated operator and workflow context are required before scans can count as real laundry operations.

### 5.4.5 — Offline, restart, and revocation proof — implemented and live-verified

- Automated tests prove an enrolled source obtains durable local acceptance with forwarding disabled and without cloud or identity configuration. Cloud and Keycloak are not part of the local authentication path.
- The exact enrolled source cookie survives gateway application restart. The browser obtains a fresh antiforgery token, retries the unchanged event, receives `alreadyAcceptedLocally`, and leaves exactly one observation/outbox pair.
- Local source revocation immediately rejects new submissions while preserving already queued work. Missing, malformed/tampered, cross-tenant/cross-plant, and permissionless credentials plus missing/invalid antiforgery and insecure transport store nothing.
- A copied browser cookie cannot cross the configured gateway tenant/plant scope. Because it is intentionally an opaque bearer credential, an exact copy used against the same gateway is valid until expiry or local revocation; stronger copy resistance requires a managed device-bound credential such as a client certificate.
- A repeatable live helper started the real Development gateway with cloud forwarding disabled, enrolled and accepted a synthetic observation, restarted the process, proved idempotent retry with the same cookie, revoked only that synthetic source in plant PostgreSQL, and proved a new observation returned 401 and was absent from storage. It stopped the gateway and removed its temporary logs afterward.

### 5.4.6 — First real adapter proof, after hardware selection

- Confirm whether the adapter is in-process, a local managed process, or another LAN computer.
- Validate certificate provisioning and private-key storage on its actual OS.
- Add the dedicated mutual-TLS listener and certificate authenticator only if that model fits.
- Run simulator tests first, then an explicit real-device suite.

## Acceptance criteria for checkpoint 5.4

- A newly enrolled simulated browser station submits over trusted HTTPS.
- Missing, invalid, expired, revoked, and wrong-scope credentials store no observation or outbox row. An exact bearer-cookie copy on the same gateway is not distinguishable from the original browser and remains valid until expiry or revocation.
- The gateway derives and enforces tenant/plant/station scope from the local trusted source. Optional equipment attribution is not authentication proof.
- Dedicated, flexible, and default-with-switching station behavior remains possible; authentication does not choose the operation.
- WAN, cloud, and Keycloak outages do not stop a valid enrolled station from obtaining durable local acceptance.
- Non-Development enrollment and privilege changes are not possible through an offline or unauthenticated bypass; the temporary Development bootstrap remains loopback-only and explicitly attributed as a system action.
- Credential material and enrollment codes never appear in Git, logs, scan JSON, or API error bodies.
- Rotation supports a bounded overlap and revocation without rewriting historical evidence.
- Tests cover outage, restart with the same credential, retry, duplicate, concurrent enrollment, wrong scope, CSRF, and revocation cases.
- Documentation explains certificate trust, enrollment, rotation, lost-station recovery, and the central-revocation delay.

## Not included in this slice

- Selecting the human login, operator attribution, active scan-session, confirmation, or offline human-session mechanisms.
- Authorization for synchronization diagnostics or replay.
- A station-management UI.
- Changing the raw scan contract to encode workflow operation.
- Automatically trusting arbitrary hardware or browser clients.
- Production certificate authority deployment before the pilot environment is known.
- Opening the gateway to the plant LAN before authenticated HTTPS is verified.

## Decisions needed before implementation

1. Pilot station operating system and managed browser: for example Windows kiosk with Edge/Chrome, Android devices, or a mixture.
2. Whether the operator PWA can be served directly by the plant gateway under the same HTTPS origin. This plan recommends yes.
3. First fixed RFID/barcode device and whether its adapter runs inside the gateway, on the gateway computer, or on another LAN computer.
4. Maximum expected internet outage and how long an enrolled station must run without central contact.
5. Who installs gateway certificate trust and who is allowed to enroll or revoke a station at the pilot plant.

## References

- [ASP.NET Core certificate authentication](https://learn.microsoft.com/aspnet/core/security/authentication/certauth)
- [ASP.NET Core antiforgery protection](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery)
- [ASP.NET Core SameSite cookie guidance](https://learn.microsoft.com/aspnet/core/security/samesite)
- [OAuth browser-application best current practice, RFC 10017](https://www.rfc-editor.org/rfc/rfc10017.html)
- [OAuth mutual-TLS standard, RFC 8705](https://www.rfc-editor.org/rfc/rfc8705)
- [Microsoft Edge managed client-certificate selection](https://learn.microsoft.com/deployedge/microsoft-edge-policies/autoselectcertificateforurls)
- [PWA HTTPS installation requirement](https://developer.mozilla.org/docs/Web/Progressive_web_apps/Guides/Making_PWAs_installable)
