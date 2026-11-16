import { defineConfig, devices } from "@playwright/test";

// Phase 4.0 browser smoke tests. The production bundle is served by vite preview; the API address points at a closed local
// port on purpose, so the "service unreachable" state is deterministic and no backend or database is needed.
// Full-stack end-to-end tests (real API and PostgreSQL) are a later extension and are NOT covered here.
export default defineConfig({
  testDir: "./e2e",
  timeout: 30_000,
  retries: 0,
  forbidOnly: !!process.env.CI,
  reporter: [["list"]],
  use: { baseURL: "http://127.0.0.1:4173", ...devices["Desktop Chrome"], channel: process.env.E2E_CHANNEL ?? "chromium", trace: "retain-on-failure", screenshot: "only-on-failure" },
  webServer: {
    command: "npm run build && npm run preview -w @hasm/web -- --host 127.0.0.1 --port 4173 --strictPort",
    url: "http://127.0.0.1:4173",
    timeout: 240_000,
    reuseExistingServer: false,
    env: { VITE_API_BASE_URL: "http://127.0.0.1:9" },
  },
});
