import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,

  {
    rules: {
      /*
       * CLAUDE.md non-negotiable #4 and blue-shell-frontend-engineering-rules §3, §11:
       * no PHI in localStorage or sessionStorage. Both are plaintext and have no expiry
       * story, so nothing clinical may touch them. Offline drafts use encrypted IndexedDB
       * instead (DECISIONS.md D005).
       *
       * This is a lint rule rather than a code-review habit because it is the kind of
       * mistake that arrives inside an innocuous "remember the last filter" change and
       * is invisible in a diff unless someone is looking for it.
       */
      "no-restricted-globals": [
        "error",
        {
          name: "localStorage",
          message:
            "localStorage is prohibited — plaintext, no expiry. Use encrypted IndexedDB (lib/offline). See DECISIONS.md D005.",
        },
        {
          name: "sessionStorage",
          message:
            "sessionStorage is prohibited — plaintext, no expiry. Use encrypted IndexedDB (lib/offline). See DECISIONS.md D005.",
        },
      ],
      "no-restricted-properties": [
        "error",
        {
          object: "window",
          property: "localStorage",
          message:
            "localStorage is prohibited — plaintext, no expiry. Use encrypted IndexedDB (lib/offline). See DECISIONS.md D005.",
        },
        {
          object: "window",
          property: "sessionStorage",
          message:
            "sessionStorage is prohibited — plaintext, no expiry. Use encrypted IndexedDB (lib/offline). See DECISIONS.md D005.",
        },
      ],
    },
  },

  globalIgnores([
    ".next/**",
    "out/**",
    "build/**",
    "coverage/**",
    "playwright-report/**",
    "test-results/**",
    "next-env.d.ts",
  ]),
]);

export default eslintConfig;
