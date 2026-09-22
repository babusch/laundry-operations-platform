# Station PWA

This is the React/Vite foundation for the future shop-floor operator application.

The current slice is intentionally non-operational: it renders an accessible Development simulator shell and checks whether the local gateway can reach plant storage. It sends no scans, performs no enrollment, and provides no offline queue or service worker. Those behaviors will be added only after their requirements and operator feedback states are reviewed.

From the repository root:

```powershell
corepack pnpm dev:station
corepack pnpm test:station
corepack pnpm build:station
```

For fast visual development, run the plant database, gateway, and Vite server in separate terminals. The station opens at `http://127.0.0.1:5173`; Vite forwards the PWA's same-origin `/api` and `/health` requests to the trusted gateway at `https://localhost:7200`. This HTTP mode is not used to prove secure-cookie enrollment.

`corepack pnpm build:station` writes the generated application to the gateway's ignored `wwwroot` directory. Start the gateway afterward and open `https://localhost:7200` for the secure integration mode used by enrollment and scan acceptance. A ready result confirms local gateway database connectivity only; it does not claim cloud connectivity or scan acceptance. Stop each foreground process with `Ctrl+C`.
