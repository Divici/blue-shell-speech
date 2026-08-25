import { defineConfig, devices } from "@playwright/test";
import { sessionSecret } from "./e2e/session";

const PORT = 3000;

/*
 * One session key for the whole run, resolved before anything starts.
 *
 * Written back into `process.env` deliberately: Playwright loads this config in the main
 * process AND in every worker, and workers inherit the environment they are spawned with —
 * so assigning here makes the `??` below a no-op in the workers and every one of them
 * agrees with the server. Without it each worker would generate its own key and mint
 * cookies the server cannot read.
 */
process.env.SESSION_SECRET ??= sessionSecret();
const SESSION_SECRET = process.env.SESSION_SECRET;

// Kept in step with the default in `e2e/api-stub.mjs` and `e2e/consultation-api.ts`.
const API_STUB_PORT = Number(process.env.API_STUB_PORT ?? 3001);

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  // Spread rather than `workers: undefined` — exactOptionalPropertyTypes distinguishes
  // "absent" from "explicitly undefined", and Playwright's type only accepts the former.
  ...(process.env.CI ? { workers: 1 } : {}),
  reporter: process.env.CI
    ? ([["github"], ["html", { open: "never" }]] as const)
    : "list",

  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: "on-first-retry",
    screenshot: "only-on-failure",
  },

  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },

    /*
     * WebKit is not optional here (DECISIONS.md D023).
     *
     * Every hard constraint in the dictation design is a Safari behaviour: no Background
     * Sync API, 7-day storage eviction outside installed PWAs, MediaRecorder emitting
     * mp4/AAC rather than webm. Testing only Chromium exercises the one browser whose
     * limitations do not apply — and Michelle's phone is not running Chromium.
     */
    { name: "webkit", use: { ...devices["Desktop Safari"] } },

    // The real dictation target: an iPhone-sized WebKit viewport.
    { name: "mobile-safari", use: { ...devices["iPhone 14"] } },
  ],

  /*
   * TWO SERVERS: the site, and a stand-in for the .NET API.
   *
   * The consultation form no longer confirms anything it has not stored, so the browser
   * flow depends on a POST reaching an API that answers. This job has no database and no
   * .NET runtime, and standing a real API up here would duplicate the `api` job's signal
   * and make the front-end suite fail on a migration. `e2e/api-stub.mjs` says at length
   * what the stand-in is and what it must never become — everything the API DECIDES is
   * asserted against real SQL Server in Practice.Api.Tests.
   *
   * API_BASE_URL is set here rather than left to `.env.local` or the CI job, both of which
   * point at a real API that is not running. Playwright merges this over process.env, and
   * Next does not overwrite an environment variable that is already set — so this wins in
   * both places without either file changing.
   */
  webServer: [
    {
      command: "node e2e/api-stub.mjs",
      url: `http://127.0.0.1:${API_STUB_PORT}/_health`,
      reuseExistingServer: !process.env.CI,
      timeout: 30_000,
    },
    {
      command: "npm run build && npm run start",
      url: `http://localhost:${PORT}`,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      env: {
        API_BASE_URL: `http://127.0.0.1:${API_STUB_PORT}`,
        /*
         * The server and the tests encrypt session cookies with the SAME key.
         *
         * `e2e/session.ts` mints a provider session so the authenticated screens are
         * reachable without an Identity store, and a JWE the server cannot decrypt is a
         * redirect to `/login` rather than an error — which would fail the tests as an
         * unexplained wrong screen. Resolved once, above, and pushed into `process.env` so
         * that Playwright's worker processes inherit the same value they pass to the
         * server here. Next does not overwrite an environment variable that is already
         * set, so this wins over `.env.local` — and where `.env.local` was the source, the
         * two are the same string anyway.
         */
        SESSION_SECRET,
      },
    },
  ],
});
