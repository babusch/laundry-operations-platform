# Station PWA

This is the React/Vite foundation for the future shop-floor operator application.

The current slice is intentionally non-operational: it renders an accessible Development simulator shell, sends no scans, performs no enrollment, and provides no offline queue or service worker. Those behaviors will be added only after their requirements and operator feedback states are reviewed.

From the repository root:

```powershell
corepack pnpm dev:station
corepack pnpm test:station
corepack pnpm build:station
```

The development server prints the local URL to open. Stop it with `Ctrl+C`.
