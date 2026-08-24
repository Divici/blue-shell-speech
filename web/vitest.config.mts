import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import path from "node:path";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./vitest.setup.ts"],
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
