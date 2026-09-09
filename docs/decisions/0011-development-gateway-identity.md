# 0011 — Reproducible development gateway identity

Status: Accepted
Date: 2026-09-09

## Context

The local identity service is verified. Before application authentication is changed, we need to prove a separate application realm and least-privilege machine token can be reproduced without manual console configuration.

## Decision

Import a `laundry-development` realm at Keycloak startup when it does not already exist. Register a bearer-only `laundry-cloud-api` audience and one confidential `gateway-development` service account. The gateway identity receives only `scans.ingest` and fixed synthetic tenant/plant claims matching existing development contracts. Tokens last five minutes and contain the cloud API audience.

Supply the disposable client secret through private `.env`; never commit or print it. Use a small idempotent local initialization script to reconcile the gateway's explicit role scope when the realm already exists, because startup import skips existing realms. Use a separate script to request and structurally inspect a token without storing or displaying the credential or token. Realm import is bootstrap configuration, not backup or a general update mechanism.

The secret authenticator is limited to this local proof and is not production credential approval. Evaluate and test per-gateway asymmetric authentication and key storage before connecting deployment-grade forwarding, as proposed by ADR 0009.

## Consequences

Developers add a third generated local credential. The identity proves issuer, audience, permission, and registered source claims, but the cloud API does not trust or validate it yet. No human users belong in this realm during this slice.

References: [Keycloak realm import](https://www.keycloak.org/server/importExport), [Keycloak service accounts](https://www.keycloak.org/docs/latest/server_admin/#_service_accounts).

Verified on 2026-09-09: realm import and scope reconciliation succeeded. Invalid client credentials returned 401; a valid client-credentials request returned a five-minute token with the expected issuer, cloud API audience, `scans.ingest` application permission, and synthetic tenant/plant registration. No application trusts that token yet.
