import { defineConfig, devices } from "@playwright/test";

const PORT = 3000;

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

  webServer: {
    command: "npm run build && npm run start",
    url: `http://localhost:${PORT}`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
  },
});
