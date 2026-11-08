import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Fixed port: the Tauri shell loads this dev server (see src/desktop/src-tauri/tauri.conf.json).
export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  server: {
    port: 5173,
    strictPort: true,
    // Dev-server file access is limited to the frontend workspace and the official branding folder only
    // (not the whole repository, so docs/, backend config, .env files etc. are never served).
    fs: {
      allow: [
        fileURLToPath(new URL("../..", import.meta.url)),
        fileURLToPath(new URL("../../../../assets/branding", import.meta.url)),
      ],
    },
  },
  test: { environment: "jsdom", setupFiles: ["./src/test-setup.ts"] },
});
