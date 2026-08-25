import { test as base, expect } from "@playwright/test";

/**
 * Every test is a different visitor.
 *
 * The consultation action rate-limits by hashed source address, holding the counters in
 * module memory for the life of the `next start` process (`lib/rate-limit.ts`). Locally
 * there is no `x-forwarded-for` header, so `clientKey()` falls back to a single constant
 * and EVERY test in EVERY project shares one bucket of five submissions per ten minutes.
 *
 * The suite submits the form eight times. Whichever three tests happened to run last were
 * answered with the rate-limit message instead of the behaviour they assert — which is why
 * the two consultation tests failed only under full-suite load and passed 3/3 alone. Shared
 * state, not a race, and not a timing problem: adding a retry or a longer timeout would
 * have made it worse by spending more of the budget.
 *
 * Behind Container Apps ingress the header is always set and real parents arrive from
 * different addresses, so giving each test its own is what the deployed topology actually
 * looks like — not a way of switching the limiter off. The limiter's own behaviour is
 * asserted where it is deterministic: `lib/rate-limit.test.ts` and
 * `app/consultation/actions.test.ts`.
 */

/**
 * A fresh address per test, from RFC 6598 shared address space — reserved, never routable,
 * and 4 million wide, so distinctness does not depend on how many tests exist.
 *
 * Random rather than derived from the test id on purpose: `reuseExistingServer` means a
 * local re-run talks to a server whose buckets are still warm, and a stable per-test
 * address would accumulate a count on every run until the fifth one started failing.
 */
function syntheticClientAddress(): string {
  const octet = () => Math.floor(Math.random() * 256);
  return `100.${64 + Math.floor(Math.random() * 64)}.${octet()}.${octet()}`;
}

export const test = base.extend({
  page: async ({ page }, use) => {
    await page.setExtraHTTPHeaders({ "x-forwarded-for": syntheticClientAddress() });
    await use(page);
  },
});

export { expect };
