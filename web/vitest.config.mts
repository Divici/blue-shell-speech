import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import path from "node:path";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./vitest.setup.ts"],
    /*
     * The suite runs in the practice's timezone, not the machine's.
     *
     * Every timestamp this app receives is UTC and every one it renders is
     * America/New_York, so a suite running in UTC agrees with a bug: `new Date(value)` on
     * a timestamp with no zone designator is correct in UTC and four hours out in Maryland.
     * That is exactly how a live defect survived — a schedule endpoint serialising
     * `startUtc` without a Z, and a test suite that could not tell.
     *
     * Pinned rather than left to the machine so a laptop, a container, and CI all report
     * the same thing. The zone is deliberately the one the product renders in: it is where
     * the arithmetic has to be right.
     */
    env: { TZ: "America/New_York" },
    // e2e/ belongs to Playwright. Without this, Vitest collects the specs and fails
    // on Playwright's fixtures rather than reporting anything useful.
    exclude: ["node_modules/**", ".next/**", "e2e/**"],
    coverage: {
      provider: "v8",
      reporter: ["text", "lcov"],
      // No coverage threshold, deliberately. TEST_STRATEGY.md: the question is whether a
      // test would have caught a real defect, not whether a line was executed. A threshold
      // rewards tests written to move a number.
      exclude: ["**/*.config.*", "**/.next/**", "e2e/**", "**/*.d.ts"],
    },
  },
  resolve: {
    alias: { "@": path.resolve(__dirname, ".") },
  },
});
