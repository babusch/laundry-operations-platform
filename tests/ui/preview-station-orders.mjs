// Test-only visual fixture server. Never proxies to real services or changes gateway permissions.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { resolve, sep, extname } from "node:path";
const root = resolve("artifacts/station-orders-preview");
const ids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222", "33333333-3333-4333-8333-333333333333"];
const items = ids.map((id, i) => ({ id, orderNumber: `DEMO-QA-00${i + 1}`, customer: { id: ids[0], code: `QA-0${i + 1}`, displayName: ["Synthetic hotel customer", "Synthetic cleaning team", "Synthetic customer with a longer display name"][i] }, plannedReceivingDate: "2030-01-02", plannedDeliveryDate: "2030-01-04", status: "planned", version: 1 }));
let reads = 0;
const json = (response, value, status = 200) => { response.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" }); response.end(JSON.stringify(value)); };
createServer(async (request, response) => {
  const url = new URL(request.url, "http://127.0.0.1:49173");
  const view = new URL(request.headers.referer ?? url.href).searchParams.get("state") ?? "ready";
  if (url.pathname === "/health/ready") { response.end("Healthy"); return; }
  if (url.pathname === "/api/source-session") { json(response, { sourceId: ids[0], stationId: ids[1], permissions: ["scans.submit", "orders.read"], antiforgeryToken: "synthetic-only", antiforgeryHeaderName: "X-Laundry-CSRF" }); return; }
  if (url.pathname === "/api/order-plans") {
    reads++;
    if (view === "loading") { setTimeout(() => { if (!response.destroyed) json(response, { code: "fixture-timeout" }, 503); }, 20000); return; }
    if (["denied", "disabled", "unavailable"].includes(view) || (view === "changed" && url.searchParams.has("cursor"))) { json(response, { code: "fixture-only" }, { denied: 403, disabled: 404, unavailable: 503, changed: 409 }[view]); return; }
    if (view === "refresh-failure" && reads > 2) { json(response, { code: "fixture-only" }, 503); return; }
    json(response, { items: ["empty", "not-downloaded"].includes(view) ? [] : url.searchParams.has("cursor") ? items.slice(1) : ["paged", "changed"].includes(view) ? items.slice(0, 1) : items,
      hasMore: ["paged", "changed"].includes(view) && !url.searchParams.has("cursor"), nextCursor: ["paged", "changed"].includes(view) && !url.searchParams.has("cursor") ? "fixture-next" : null,
      lastSuccessfulPageAtUtc: view === "not-downloaded" ? null : "2030-01-01T12:00:00Z" }); return;
  }
  if (url.pathname === "/api/sync/summary") { json(response, { asOfUtc: "2030-01-01T12:00:00Z", pending: 0, synchronized: 0, needsAttention: 0, oldestPendingAgeSeconds: null, lastCloudReceiptAtUtc: null, forwardingEnabled: false, worker: { lastIterationAtUtc: null, lastIterationError: null } }); return; }
  if (url.pathname === "/api/sync/events") { json(response, { items: [], nextOffset: null }); return; }
  if (url.pathname.startsWith("/api/")) { json(response, { code: "fixture_server_read_only" }, 403); return; }
  const file = resolve(root, "." + (url.pathname === "/" ? "/index.html" : decodeURIComponent(url.pathname)));
  if (!file.startsWith(root + sep)) { response.writeHead(403); response.end(); return; }
  try {
    let body = await readFile(file);
    if (file.endsWith("index.html")) { reads = 0; body = Buffer.from(body.toString().replace("<body>", '<body><p style="margin:0;padding:.5rem 1rem;color:#d4a84f;background:#071a21;font:14px system-ui">SYNTHETIC VISUAL TEST · No real gateway, orders or permissions</p>')); }
    response.writeHead(200, { "Content-Type": ({ ".html": "text/html", ".js": "text/javascript", ".css": "text/css" })[extname(file)] ?? "application/octet-stream", "Cache-Control": "no-store" }); response.end(body);
  } catch { response.writeHead(404); response.end(); }
}).listen(49173, "127.0.0.1", () => process.stdout.write("Synthetic station-order preview: http://127.0.0.1:49173/?state=ready (loopback only; no real services)\n"));
