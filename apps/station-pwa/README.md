# Station PWA

This is the React/Vite foundation for the future shop-floor operator application.

The current slice is intentionally non-operational: it renders an accessible Development simulator shell and checks whether the local gateway can reach plant storage. It sends no scans, performs no enrollment, and provides no offline queue or service worker. Those behaviors will be added only after their requirements and operator feedback states are reviewed.

From the repository root:

```powershell
corepack pnpm dev:station
corepack pnpm test:station
corepack pnpm build:station
```

Run the plant database, gateway, and station development server in separate terminals. The station opens at `http://127.0.0.1:5173` and its development-only `/gateway` proxy reaches the trusted gateway at `https://localhost:7200`. A ready result confirms local gateway database connectivity only; it does not claim cloud connectivity or scan acceptance. Stop each foreground process with `Ctrl+C`.
