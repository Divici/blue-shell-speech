/**
 * The values a test and the stub have to agree on, and nothing else.
 *
 * SEPARATE FROM `api-stub.mjs` BECAUSE THAT FILE LISTENS ON IMPORT. A spec that imported
 * the stub to read a constant started a second copy of it inside the Playwright worker and
 * the whole run died with `EADDRINUSE: 127.0.0.1:3001` before a single test executed. A
 * module of constants has no side effect to trip over.
 *
 * The alternative was to copy the strings into the spec, the way `consultation-api.ts`
 * copies the port — and a duplicated trigger is a trigger that silently stops triggering
 * the day somebody changes one of the two. The test then passes while asserting a fast
 * path, which is the exact failure this suite exists to catch.
 */

/**
 * Requests naming these are answered SLOWLY, so a loading state can be observed.
 *
 * KEYED ON REQUEST CONTENT, NEVER ON A FLAG. Three browser projects run in parallel
 * against one stub instance, so "make the next request slow" would be a race between
 * workers. A reserved date and a reserved address are deterministic per request and need
 * no shared state — the same rule `UNSTORABLE_CHILD` follows for failures.
 */
export const SLOW_DAY = "2026-01-09";
export const SLOW_LOGIN_EMAIL = "slow@synthetic.test";

/**
 * How long that is.
 *
 * A tenth of the real thing. `api` scales to zero and its database auto-pauses, so a first
 * request is measured in tens of seconds — docs/PERFORMANCE.md measures a ~22 s cold
 * start. Two and a half seconds has the same shape and does not make the suite unbearable.
 */
export const SLOW_MS = 2_500;

/** A submission naming this child is answered 503, as an unreachable practice would be. */
export const UNSTORABLE_CHILD = "Unstorable";
