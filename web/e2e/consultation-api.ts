import type { APIRequestContext } from "@playwright/test";

/**
 * Reading what the API stand-in was sent.
 *
 * The stub itself is `e2e/api-stub.mjs`; this is the only thing the specs need to know
 * about it. See that file for what it is and — more importantly — what it must never
 * become.
 */

// Kept in step with the default in `e2e/api-stub.mjs`.
const STUB = `http://127.0.0.1:${process.env.API_STUB_PORT ?? 3001}`;

/**
 * The child's name that makes the stand-in answer 503.
 *
 * Chosen by content rather than by a "fail next request" flag, because three browser
 * projects run against one stub in parallel and a flag would be a race between them.
 */
export const UNSTORABLE_CHILD = "Unstorable";

/**
 * How many submissions the API was sent for this child.
 *
 * Every test uses a distinct name, so this counts that test's own submissions and nobody
 * else's. Asserting it is what stops "the browser showed a thank-you" from passing when
 * nothing was ever posted — the assertion the confirmation is now making on the parent's
 * behalf.
 */
export async function submissionsReceivedFor(
  request: APIRequestContext,
  childFirstName: string,
): Promise<number> {
  const response = await request.get(
    `${STUB}/_received?child=${encodeURIComponent(childFirstName)}`,
  );
  const body = (await response.json()) as { count: number };
  return body.count;
}
