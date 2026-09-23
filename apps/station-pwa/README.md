# Station PWA

This is the React/Vite foundation for the future shop-floor operator application.

The current slice renders an accessible Development simulator shell, checks whether the local gateway can reach plant storage, and then checks whether this browser already has a valid enrolled-source session. From the gateway-hosted HTTPS origin only, an unenrolled browser can run the existing Development setup flow. The PWA exchanges a short-lived, single-use code for a secure cookie without displaying or retaining enrollment material. An enrolled browser can submit synthetic RFID or barcode input as a raw observation through the real durable gateway endpoint. RFID is the simulator default for the pilot direction, while barcode remains available. A read-only synchronization audit shows waiting, synchronized, and needs-attention counts plus the five most recent observation states without displaying event IDs or RFID/barcode values. It signs in no operator and provides no production workflow, offline queue, or service worker.

From the repository root:

```powershell
corepack pnpm dev:station
corepack pnpm test:station
corepack pnpm build:station
```

For fast visual development, run the plant database, gateway, and Vite server in separate terminals. The station opens at `http://127.0.0.1:5173`; Vite forwards the PWA's same-origin `/api` and `/health` requests to the trusted gateway at `https://localhost:7200`. This HTTP mode does not offer browser enrollment and is not used to prove secure-cookie setup.

`corepack pnpm build:station` writes the generated application to the gateway's ignored `wwwroot` directory. Start the gateway afterward and open `https://localhost:7200` for the secure integration mode used by browser enrollment, enrolled-source sessions, and scan acceptance. A gateway-ready result confirms local database connectivity only. The second status reports whether the browser has an enrolled-source cookie; it deliberately does not display internal source or station identifiers and does not claim operator authentication.

After enrollment, choose RFID or barcode and send the synthetic identifier. The simulator creates one immutable `submit-scan.v2` request and shows success only when the gateway confirms durable local storage. A connection failure can leave the commit result unknown; in that state **Retry same scan** locks and resends the exact technology, identifier, and event so gateway idempotency prevents duplication. This Development control records only a raw observation—not receiving, sorting, packing, dispatch, or another laundry action. Stop each foreground process with `Ctrl+C`.

The synchronization audit loads automatically after enrollment and after an accepted simulated scan. Use **Refresh audit** to request another snapshot. It is metadata-only and read-only: replay and other administrative actions remain outside the station interface.

The station bundles English and Swedish translations. It first uses a saved browser preference, then a supported browser language, and falls back to English. The visible language selector changes the interface immediately, updates the document language for accessibility, and remembers the choice in local browser storage. Translation does not require the gateway or cloud to be online.
