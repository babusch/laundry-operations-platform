# Station-to-gateway identity and access plan

Date: 2026-09-14  
Status: Source-identity direction approved 2026-09-20; operator authentication, workflow behavior, deployment details, and implementation remain under review

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

All authenticators produce the same internal `SourceIdentity`: credential ID, tenant ID, plant ID, station ID, device ID, principal type, permission set, and registration status. The scan module depends on that project-owned result, not on cookies, certificates, Keycloak types, or vendor SDKs.

Start implementation with one simulated browser station using the default cookie mechanism. Keep station authentication replaceable so a managed installation can use a workstation certificate without changing scan-domain rules. Add the adapter certificate authenticator only after confirming the first fixed-reader operating system, SDK process model, and certificate deployment capability. This keeps the boundary extensible without speculatively integrating hardware.

Every operational request ultimately needs both trustworthy source identity and separately authorized human/workflow context. Operator login does not identify the station, and a station cookie or certificate does not identify a person. The exact operator sign-in, offline session, active scanning-session, and confirmation behavior is deliberately deferred for later discussion.

## Important identity separation

- A **station identity** answers where the request originated.
- A **device identity** identifies the physical reader or enrolled adapter when that can be established.
- A **human identity** answers who performed or authorized work; its mechanism is not introduced by this slice and cannot be replaced by a source credential.
- An **operation** says what the user intends to do. It remains configurable and is not implied by station identity.

For a keyboard-wedge scanner, the browser can prove the enrolled workstation installation but usually cannot cryptographically prove the scanner's manufacturer serial number. Its configured `deviceId` therefore represents that enrolled input source. A networked fixed-reader adapter can have its own independently enrolled credential.

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

1. An authorized administrator selects an existing tenant, plant, station, and configured input device and creates a short-lived, single-use enrollment code.
2. The station opens the PWA from the gateway's trusted HTTPS address and enters or scans that code.
3. The gateway consumes the code atomically and creates a high-entropy random station credential.
4. The gateway stores only a digest of the secret and returns the credential in a host-only `Secure`, `HttpOnly`, `SameSite=Strict` cookie.
5. Subsequent API requests resolve that credential against the local plant database on every request. Disabled, revoked, unknown, or expired credentials do not authenticate.
6. Unsafe cookie-authenticated requests also require ASP.NET Core antiforgery validation. CORS, JSON content type, or `SameSite` alone are not treated as complete CSRF protection.

The cookie authenticates the browser installation, not a person. Human sign-in can later add a separate user principal/session without changing the station registration.

Because checkpoint 5.5 human administration is not built yet, the first Development proof may use an explicit loopback-only command to create one enrollment code. That bootstrap must be unavailable outside Development and must not become a production administrative bypass.

Development bootstrap audits use an explicit system actor such as `local-development-bootstrap`; they must not pretend a person was authenticated. Non-Development enrollment remains disabled until checkpoint 5.5 supplies an authorized, attributable administrator.

An opaque browser cookie remains a bearer credential: copying a complete browser profile may copy it. `HttpOnly` reduces exposure to JavaScript but does not provide hardware-bound proof. Before pilot deployment, decide whether managed-browser controls are sufficient or whether the selected station fleet supports a stronger device-bound mechanism such as managed client certificates. Do not invent a custom browser signing protocol.

### Managed-browser certificate option

Where a customer centrally manages station computers and can deploy certificates and browser policy, a unique workstation client certificate may replace the station cookie. This is stronger proof of possession but requires certificate issuance, protected private-key storage, automatic browser selection, renewal, revocation, and recovery. It is an optional deployment profile rather than a prerequisite for every laundry.

URLs, IP addresses, MAC addresses, request JSON, or an operator-selected station name are not station authentication. They may assist configuration or network controls but cannot establish trusted source identity.

## Fixed-adapter enrollment flow

When a real adapter on another computer or LAN-connected host is selected:

1. Generate or provision a unique keypair for that adapter; never share one fleet-wide private key.
2. Register its certificate/public key against exactly one source registration.
3. Connect to a dedicated gateway HTTPS listener that requests and validates client certificates.
4. Validate certificate chain or explicit local trust, intended client-authentication use, validity period, and the active local registration.
5. Resolve the certificate to the same `SourceIdentity` used by browser stations.

Private keys stay in the adapter's operating-system or hardware-backed key store where available. The database stores public certificate material or a stable public-key fingerprint, never the private key. Local registration status provides immediate plant revocation without requiring an online certificate-revocation service.

An adapter running inside the gateway process uses a gateway-managed internal principal. A separate adapter process on the same gateway computer should prefer operating-system-protected IPC and service identity when the target OS supports it. Mutual TLS is primarily for a managed adapter crossing a machine or network boundary, not a requirement for every scanner process.

## Authorization of a scan

Authentication and source authorization happen before the scan body reaches durable acceptance:

1. Require an authenticated source with `scans.submit`.
2. Load its active source registration from plant PostgreSQL.
3. Derive tenant, plant, station, and device from that registration.
4. Validate the existing `submit-scan.v1` body.
5. Compare all four body source IDs with the trusted registration. A mismatch returns 403 and stores nothing.
6. Run the existing transaction that stores immutable evidence and its outbox row before returning success.

The body remains evidence supplied by the origin, but it does not grant authority. Initially retaining and comparing its source fields avoids changing the shared contract. A later contract version may remove redundant caller-selected scope if experience supports that change.

Station authentication does not lock a station to receiving, dispatch, or another operation. Operational choices and capabilities remain separate configuration. A fallback station keeps its own real station/device identity while performing an operation it is allowed and equipped to perform.

Machine credentials cannot call synchronization diagnostics, request replay, enroll another source, or act as a human. Those endpoints require their own policies.

## Local data model

Add explicit migrations for a small local source-security model:

- `source_registrations`: stable source ID, tenant/plant/station/device mapping, source kind, status, and configuration version.
- `source_credentials`: credential ID, registration ID, credential kind, secret digest or public-certificate identity, issued/expiry times, status, rotation overlap, and revocation time.
- `source_enrollment_codes`: digest, intended registration, expiry, redemption time, and single-use state.
- `source_security_audit`: append-only enrollment, credential rotation, local revoke/restore, and failed administrative actions, with an authenticated actor or an explicit Development system actor.

One source can have overlapping credentials during rotation. Reassigning a credential to another tenant, plant, station, or device is forbidden; create a new registration/credential instead. Historical scan events retain their original source fields.

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

### 5.4.1 — Trusted gateway HTTPS

- Add a Development HTTPS listener for the gateway using trusted local certificate validation.
- Keep the existing HTTP endpoint loopback-only during transition; do not open it to the LAN.
- Prove valid HTTPS succeeds and untrusted/insecure transport cannot reach the future protected route.
- Document the eventual stable plant name and certificate decision as open.

### 5.4.2 — Source registry and authentication seam

- Add migrations for source registrations, credentials, enrollment codes, and security audit.
- Add a project-owned `SourceIdentity` and authenticator abstraction.
- Seed no reusable credential in Git or application settings.
- Test tenant/plant isolation, immutable assignment, credential rotation overlap, and local revocation.

### 5.4.3 — One simulated browser enrollment

- Add the Development-only loopback bootstrap that creates one short-lived enrollment code.
- Add the HTTPS enrollment exchange and secure host-only cookie.
- Store only credential/code digests and consume codes atomically.
- Add antiforgery issuance/validation for cookie-authenticated mutations.
- Test missing, invalid, expired, reused, and concurrent enrollment attempts without logging secrets.

### 5.4.4 — Protect durable scan acceptance

- Require `scans.submit` on gateway `POST /api/scans`.
- Resolve source scope from the authenticated local registration and compare it with the body.
- Remove the configured single synthetic `DevelopmentSource` as the authorization authority.
- Preserve validation, size limits, transactionality, conflict behavior, receipts, and outbox payloads.
- Provide no unauthenticated fallback in Staging or Production.

### 5.4.5 — Offline, restart, and revocation proof

- Prove an enrolled station submits while Keycloak and the cloud are stopped.
- Prove gateway restart retains access and queued scans unchanged.
- Prove local revoke prevents new submissions even during WAN outage.
- Prove wrong source, copied/invalid credentials, CSRF, and insecure transport store nothing.
- Prove retrying the same accepted event remains idempotent.

### 5.4.6 — First real adapter proof, after hardware selection

- Confirm whether the adapter is in-process, a local managed process, or another LAN computer.
- Validate certificate provisioning and private-key storage on its actual OS.
- Add the dedicated mutual-TLS listener and certificate authenticator only if that model fits.
- Run simulator tests first, then an explicit real-device suite.

## Acceptance criteria for checkpoint 5.4

- A newly enrolled simulated browser station submits over trusted HTTPS.
- Missing, invalid, expired, revoked, and wrong-source credentials store no observation or outbox row.
- The gateway derives and enforces tenant/plant/station/device scope from local registration.
- Dedicated, flexible, and default-with-switching station behavior remains possible; authentication does not choose the operation.
- WAN, cloud, and Keycloak outages do not stop a valid enrolled station from obtaining durable local acceptance.
- Non-Development enrollment and privilege changes are not possible through an offline or unauthenticated bypass; the temporary Development bootstrap remains loopback-only and explicitly attributed as a system action.
- Credential material and enrollment codes never appear in Git, logs, scan JSON, or API error bodies.
- Rotation supports a bounded overlap and revocation without rewriting historical evidence.
- Tests cover outage, restart, retry, duplicate, concurrent enrollment, wrong scope, CSRF, and revocation cases.
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
