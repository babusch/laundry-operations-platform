# Station operator sessions and inactivity lock

Status: initial behavior and password-based unlocking approved; technical authentication/offline design and implementation remain pending.

## Approved behavior (2026-10-18)

- An operator normally signs in once per shift. Do not design routine repeated full sign-ins into ordinary work.
- A station automatically locks after **15 minutes of inactivity** by default. The laundry may configure the duration; no settings API or control exists yet.
- Deliberate operator interaction counts as activity. Background RFID reads do not reset the timer: stray reads must not keep an unattended station unlocked. Scanner keystrokes must not be assumed to prove deliberate human interaction either; exact input classification needs implementation review.
- Locking pauses operational scanning but preserves the operator session and saved work. Locking is different from signing out.
- Unlocking requires verifying the same operator with their account password. Changing operators remains a deliberate handover to a separately authenticated operator, not entering someone else's password to resume the previous operator's session.
- Signing out ends the operator session, not the shared order. Saved work and open shared orders remain intact.
- A station supports one active operator at a time across its connected input sources. Source identity and operator identity remain separate; several physical readers may use the same station.

The inactivity timer is not a shift length, maximum session lifetime, cloud-token lifetime or offline allowance. Do not silently end overnight shifts at midnight or invent an eight-hour session expiry from this decision.

## Security and offline boundaries

The gateway must enforce the locked/signed-out state when authorizing future business commands. A disabled browser button alone is insufficient. Reader signals received without required human/workflow authorization cannot count as receiving/dispatch or be automatically applied to an order after unlocking. Previously accepted durable work is not undone.

An internet or cloud-identity outage must not immediately end an otherwise valid locally authorized session. This does not permit indefinite human access or override expiry/local revocation. Exact local validity, revocation delay, session recovery after restart and shift/session lifetime remain undecided.

Offline unlock, a new offline sign-in and offline operator handover each require a reviewed mechanism. Existing enrolled-source offline acceptance does not solve these human-authentication requirements. Keep the machine-access policy in [ADR 0009](decisions/0009-identity-permissions-and-offline-access.md) intact; never copy central passwords locally or assume an expired cloud token remains valid.

## Initial unlock method and future option

The user chose **password-based unlocking** as the initial method (2026-10-18). Unlock must require fresh password verification; an existing identity-provider session must not silently unlock the station without it. This approves the user-facing method, not a custom password endpoint, password grant, local password copy or offline verifier. Choose the technical integration with the existing identity setup separately.

**Staff badges are a potential future implementation**, not part of the initial build. Review badge technology, binding to an operator, verification strength, lost/shared badge revocation, enrollment/replacement and offline behavior before adoption. Do not assume an ordinary textile RFID tag or unverified badge identifier is adequate proof of a person. A personal PIN is not selected for this slice.

## Proposed development slice — mapped, not approved for implementation (2026-10-18)

The user requested a map of a local Keycloak sign-in/password-unlock proof. Approval to map is not approval to choose production identity hosting, create accounts/grants or implement the entire station workflow.

### Operator experience

1. An enrolled station offers **Sign in**. Keycloak verifies the account password; the gateway establishes a distinct operator session and the PWA displays a safe operator name. Account authentication alone grants no plant membership or operational permission.
2. Once authorized station sessions are added, the operator can deliberately **Lock station**, or the gateway locks it after 15 minutes without qualifying human activity. Background reader traffic, API polling and merely keeping a tab open do not count. Locking does not complete orders or undo durable work.
3. **Unlock** requests fresh password verification for the same account, not silent SSO. Cancel, wrong password, another account or unavailable identity service leaves the station locked. Different-account handover is separate; it cannot inherit the prior person's working context silently.
4. **Sign out** ends/revokes the local operator session across the station's tabs/sources. It leaves source enrollment, accepted work and shared orders intact. It must be possible locally even when Keycloak is unavailable; it is not provider-wide logout.

English/Swedish states must distinguish signed out, verifying, unlocked, locked and verification unavailable. Clearly state that no scans count as laundry operations yet. Design skills require stable controls/notice space, keyboard focus, clear text/icon feedback and preservation of saved work; no decorative motion or full-workflow redesign is needed for this proof.

### Technical boundary

- Reuse the existing local Keycloak service, but propose a separate station OIDC client and gateway-owned named authentication handlers/cookie. Do not reuse the management session, callback, permissions or its fixed 15-minute **expiry** as the operator inactivity **lock**.
- Use standard framework authorization-code/PKCE handling, exact callback/return targets and verified issuer/subject. Password entry stays with Keycloak; no direct password grant, gateway password form, browser token storage or locally copied password verifier is introduced.
- Use distinct cookie names and explicit authentication schemes; localhost ports alone do not isolate cookies. Source enrollment and the operator session are both required where the eventual protected workflow applies. Caller-supplied operator IDs or names are never authority.
- A gateway-owned station-session record, explicit state/version checks and atomic transitions are proposed for the later lock slice. One active operator per tenant/plant/station must hold across browsers/adapters. Old login/unlock callbacks must not revive a signed-out, superseded or revoked session. Persisted state/audit changes require a migration and transactional tests.
- The gateway owns inactivity enforcement; browser status/activity reporting is not authority to extend an already expired/locked session. Review scanner-input classification and throttled deliberate-activity reporting before implementation. Background RFID/keyboard-wedge input must not accidentally defeat locking.
- Future receiving/dispatch commands must enforce active, unlocked and authorized operator context on the server. This proof does not add those commands, change `scan.observed.v2`, count raw observations or retrofit operator identity into old events. Existing development raw-scan infrastructure remains explicitly non-operational; UI blocking must not be advertised as complete reader/workflow enforcement.
- Login, lock, unlock, sign-out and rejected security transitions need bounded, append-only audit evidence with correlation and trusted identity/scope, never passwords, tokens or raw tag values.

### Explicit outage limitation

No internet while local Keycloak and its required database remain reachable: password login/unlock should still work for local test accounts without external identity dependencies; prove this in a controlled test, not by assuming an already signed-in tab is sufficient.

Keycloak unavailable: an already locally valid unlocked session is not immediately ended, but its ordinary inactivity/expiry/revocation rules still apply. New sign-in and password unlock fail safely with an actionable error. Locking/sign-out remain local. Recovery permits a fresh verification attempt; it never silently unlocks. No deferred reads captured while locked automatically become business actions.

This deliberately cannot demonstrate production multi-day, multi-shift operator availability through identity-service failure. Plant-local authentication availability or a separately reviewed offline mechanism must be selected before production receiving is described as offline-ready. No emergency bypass, disabled timeout, expired-token acceptance or cloud-password cache is approved.

### Build order and checks

1. **Identity-only proof first:** disabled-by-default Development loopback HTTPS sign-in, safe session-status and local sign-out, with a separate client/cookie. Test real Keycloak login in addition to mocked middleware. Do not add a grant or activate operator-backed scanning.
2. **Station-session/lock foundation:** review the storage/API/audit contract, membership/permissions, session maximum lifetime, failed-attempt policy, activity classification, restart recovery and takeover races before implementing atomic station ownership and the inactivity lock. Shift duration and the maximum session lifetime are not set by this map; the identity-only proof needs an explicitly bounded test lifetime, not unlimited sliding sessions.
3. **Password unlock and small PWA integration:** add fresh same-person verification, explicit safe failures and local sign-out/lock controls. Preserve the current read-only preview and existing dark bilingual design. Badge unlock remains future work.

Tests must cover wrong/missing source and scope, independent authentication/authorization, CSRF/callback protections, wrong/silent/replayed/stale authentication, multiple tabs/concurrent operators, deliberate activity versus reader/poll traffic, inactivity boundaries, local sign-out/revocation, restart/recovery, identity outage and recovery. A cancelled or failed request must not unlock or take over a station. After unlock, session status—not an old browser state—determines what is permitted.

The next proposed implementation is **only step 1**, not all three steps. A short ADR must capture the new gateway human-authentication boundary before code/dependency changes. Exact contracts/configuration and scoped setup approval follow separately; no credentials or live services are changed by this plan.

## Remaining decisions

Design password verification and its offline behavior, including what happens if the station locks while the identity provider is unavailable. Review failed-attempt limits, session validity, handover and recovery before implementation. The 15-minute inactivity lock and offline continuity requirements must be reconciled explicitly; password selection alone does not solve offline unlock.

Username/password through the existing Keycloak setup was proposed for initial development sign-in, not yet implemented for stations. Management login is not automatically an operator session or grant of scanning permission. Production session contracts, storage, audit events, settings, operator UI and tests follow separately after review.

This is documentation only. The current synthetic preview/raw-observation infrastructure still does not authenticate operators or implement inactivity locking, receiving or dispatch.
