import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    proxy: {
      "/gateway": {
        target: "https://localhost:7200",
        changeOrigin: true,
        secure: false,
        rewrite: (path) => path.replace(/^\/gateway/, ""),
      },
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
  },
});
