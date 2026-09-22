import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

const gatewayProxy = {
  target: "https://localhost:7200",
  changeOrigin: true,
  secure: false,
};

export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    proxy: {
      "/api": { ...gatewayProxy },
      "/health": { ...gatewayProxy },
    },
  },
  build: {
    outDir: "../edge/Laundry.Edge/wwwroot",
    emptyOutDir: true,
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
  },
});
