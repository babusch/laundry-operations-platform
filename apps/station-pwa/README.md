# Station PWA

This is the React/Vite foundation for the future shop-floor operator application.

The current slice is intentionally non-operational: it renders an accessible Development simulator shell, checks whether the local gateway can reach plant storage, and then checks whether this browser already has a valid enrolled-source session. It sends no scans, creates no enrollment, signs in no operator, and provides no offline queue or service worker. Those behaviors will be added only after their requirements and operator feedback states are reviewed.

From the repository root:

```powershell
corepack pnpm dev:station
corepack pnpm test:station
corepack pnpm build:station
```

For fast visual development, run the plant database, gateway, and Vite server in separate terminals. The station opens at `http://127.0.0.1:5173`; Vite forwards the PWA's same-origin `/api` and `/health` requests to the trusted gateway at `https://localhost:7200`. This HTTP mode is not used to prove secure-cookie enrollment.

`corepack pnpm build:station` writes the generated application to the gateway's ignored `wwwroot` directory. Start the gateway afterward and open `https://localhost:7200` for the secure integration mode used by enrolled-source sessions and scan acceptance. A gateway-ready result confirms local database connectivity only. The second status reports whether the browser has an enrolled-source cookie; it deliberately does not display internal source or station identifiers and does not claim operator authentication. Stop each foreground process with `Ctrl+C`.
