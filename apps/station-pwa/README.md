# Station PWA

This is the React/Vite foundation for the future shop-floor operator application.

The current slice renders an accessible Development simulator shell, checks whether the local gateway can reach plant storage, and then checks whether this browser already has a valid enrolled-source session. From the gateway-hosted HTTPS origin only, an unenrolled browser can run the existing Development setup flow. The PWA exchanges a short-lived, single-use code for a secure cookie without displaying or retaining enrollment material. An enrolled browser can submit a synthetic barcode as a raw observation through the real durable gateway endpoint. It signs in no operator and provides no production workflow, offline queue, or service worker.

From the repository root:

```powershell
corepack pnpm dev:station
corepack pnpm test:station
corepack pnpm build:station
```

For fast visual development, run the plant database, gateway, and Vite server in separate terminals. The station opens at `http://127.0.0.1:5173`; Vite forwards the PWA's same-origin `/api` and `/health` requests to the trusted gateway at `https://localhost:7200`. This HTTP mode does not offer browser enrollment and is not used to prove secure-cookie setup.

`corepack pnpm build:station` writes the generated application to the gateway's ignored `wwwroot` directory. Start the gateway afterward and open `https://localhost:7200` for the secure integration mode used by browser enrollment, enrolled-source sessions, and scan acceptance. A gateway-ready result confirms local database connectivity only. The second status reports whether the browser has an enrolled-source cookie; it deliberately does not display internal source or station identifiers and does not claim operator authentication.

After enrollment, **Send simulated barcode** creates one immutable `submit-scan.v2` request and shows success only when the gateway confirms durable local storage. A connection failure can leave the commit result unknown; in that state **Retry same scan** resends the exact event so gateway idempotency prevents duplication. This Development control records only a raw observation—not receiving, sorting, packing, dispatch, or another laundry action. Stop each foreground process with `Ctrl+C`.
