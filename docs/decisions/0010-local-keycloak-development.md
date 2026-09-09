# 0010 — Local Keycloak development service

Status: Accepted
Date: 2026-09-08

## Context

The user approved product-managed identity and a local Keycloak evaluation before connecting gateway authentication. Customers must not require an existing Microsoft identity environment. ADR 0009's broader security implementation remains under review.

## Decision

Use the official Keycloak 26.7.3 container in a separate, explicitly selected development Compose project. Use a private PostgreSQL service and independent named volume for identity data, not the cloud or plant databases. Keycloak owns initialization/upgrades of its internal schema; this does not change the explicit EF migration policy for our applications.

Use HTTPS with a locally trusted development certificate, no HTTP listener, and only a loopback-published HTTPS port. Keep credentials and exported private keys untracked. Do not configure gateway clients, laundry users, tenant mappings, or modify application authentication in this slice. The master realm is only for local administration and discovery verification; a separate application realm follows in the gateway slice.

## Consequences

Local setup needs certificate trust, two additional containers, and disposable credentials. Docker administrators can inspect container environment credentials; this is a single-developer setup, not a production secret-storage solution. Keycloak's `start` command alone does not make this a production deployment.

Production provider/hosting remains undecided. Evaluate supported asymmetric gateway authentication, credential lifecycle, deployment ownership, and recovery before connecting the gateway. Standards reduce coupling but do not make provider replacement effortless.

References: [official images](https://www.keycloak.org/server/containers), [TLS configuration](https://www.keycloak.org/server/enabletls), [release downloads](https://www.keycloak.org/downloads).
