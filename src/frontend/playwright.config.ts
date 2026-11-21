import { defineConfig, devices } from "@playwright/test";

// Phase 4.0 browser smoke tests. The production bundle is served by vite preview; the API address points at a closed local
// port on purpose, so the "service unreachable" state is deterministic and no backend or database is needed.
// parity.spec.ts runs complete flows for BOTH client transports (web cookie, desktop bearer) through a stateful in-memory mock API (e2e/mockApi.ts) installed with page.route(); it needs no backend or database.
// Full-stack end-to-end tests (real API and PostgreSQL) are a later extension and are NOT covered here.
export default defineConfig({
  testDir: "./e2e",
  timeout: 30_000,
  retries: 0,
  // Report metadata only: in pull-request jobs Playwright runs `git fetch origin <base sha>` to attach the PR diff to the report; on the self-hosted Windows runner (checkout without persisted credentials) that fetch waits forever for a credential prompt, so the test run never started (docs/CI-RUNNER.md). No test, assertion or check is affected.
  captureGitInfo: { commit: false, diff: false },
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
