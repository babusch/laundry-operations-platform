# 0018 — Gateway hosts the station PWA

Status: Accepted  
Date: 2026-09-22

## Context

Browser-source enrollment uses a gateway-issued `Secure`, `HttpOnly`, host-only cookie with strict same-site behavior. Serving the station application and gateway API from unrelated origins would add CORS, cookie, certificate, and installation complexity. Each plant also needs a compatible PWA and gateway version that continues to operate without the cloud.

## Decision

Build the React/Vite station PWA independently, then bundle its generated files into the plant gateway release. The gateway serves the PWA and its `/api` and `/health` endpoints from one local HTTPS origin. Station routes use an HTML application-shell fallback; API-like routes never fall back to HTML. The application shell is revalidated, while fingerprinted assets may be cached immutably.

Keep Vite as a fast visual-development server. In that mode, it proxies the same `/api` and `/health` paths to the trusted local gateway, so application code does not contain a development-only URL prefix. Secure-cookie enrollment and final integration verification use the gateway-hosted HTTPS build.

## Consequences

Same-origin secure cookies and antiforgery are simpler, production CORS is unnecessary for normal station traffic, and one plant installation carries a compatible UI/API pair. A station can load the UI without internet access as long as its local gateway is available. The generated PWA files become part of edge packaging and upgrade responsibility, although the frontend remains separately built and tested.

The current Vite HTTP server is not an enrollment-security boundary. Stable plant naming, certificate provisioning, service-worker caching, emergency browser storage, enrollment UI, and operator sessions remain separate decisions and later slices.

## Revisit when

A validated deployment requires independent station hosting, a native application replaces the PWA, or gateway/UI release coupling creates a measured operational problem.
