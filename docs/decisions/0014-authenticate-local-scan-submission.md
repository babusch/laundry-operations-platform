# 0014 — Authenticate local scan submission

Status: Accepted  
Date: 2026-09-20

## Context

The Development gateway durably accepted `submit-scan.v1` from any loopback caller whose body matched one configured synthetic tenant, plant, station, and device tuple. That was useful for the storage walking skeleton but was not source authentication. Browser-source enrollment now provides a locally verifiable credential, explicit `scans.submit` permission, and antiforgery protection.

The approved domain model does not require one physical device per station. A station can use several barcode and RFID inputs, and a browser receiving keyboard-like input often cannot authenticate which peripheral produced it. Separately, a trusted-source credential identifies the browser or adapter, not the human operator.

## Decision

Protect Development gateway `POST /api/scans` before reading or durably accepting its body. Require loopback trusted HTTPS, an active enrolled browser-source cookie, the explicit `scans.submit` permission, and a matching antiforgery token. Resolve tenant, plant, and station from the local trusted-source record and require the provisional v1 body to match that scope. Missing or invalid credentials return 401; missing permission or mismatched scope returns 403; invalid antiforgery returns 400. Authentication storage outages return 503 and store nothing.

Remove the configured Development device ID from the authorization boundary. The required v1 `deviceId` remains temporarily as origin-supplied compatibility metadata and is preserved unchanged in the accepted event. It neither authenticates a peripheral nor restricts a station to one scanner. A later versioned request/event design will remove mandatory device identity and add optional equipment or capture-channel attribution only when it is honest and useful.

Keep the existing validation limits, immutable observation plus outbox transaction, retry receipts, conflict handling, and cloud payload unchanged.

An authenticated source is necessary but not sufficient for a real laundry operation. Before production workflow use, an authenticated operator and active workflow context must also authorize scans that cause or confirm receiving, sorting, packing, dispatch, inventory, or other business actions. This checkpoint does not treat source authentication as operator authentication.

## Consequences

An arbitrary local process can no longer submit a durable scan merely by knowing configured IDs. A browser must first enroll and retain its secure cookie; each mutation also needs the antiforgery token. Source disable, credential revoke, or permission removal takes effect through the local plant database without cloud availability.

The v1 body still contains redundant tenant, plant, and station fields and a mandatory `deviceId`. They are compatibility evidence, not authority. The endpoint remains Development- and loopback-only while production enrollment, operator sessions, workflow authorization, stable plant certificates/names, and the v2 contract are designed.

## Revisit when

Designing `submit-scan.v2`, integrating the operator PWA, defining offline operator sessions, supporting authenticated adapters, or preparing the first plant deployment.
