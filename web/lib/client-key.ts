import "server-only";

import { headers } from "next/headers";
import { clientIdentifier, hashClientId } from "@/lib/rate-limit";

/**
 * Who the caller is, hashed — ONE derivation, now used in three places.
 *
 * It keys the consultation form's own limiter, it is the `SourceIpHash` stored on a
 * `ConsultationRequest` row (docs/DATA_MODEL.md), and it is what the API partitions its
 * login rate limit by. Deliberately the same value for all three: a second hashing scheme
 * would produce a column that correlates with nothing the limiter ever counted, and "did
 * these twelve attempts come from the same place as that enquiry" is the only question any
 * of them exists to answer (D080).
 *
 * WHY THE API CANNOT DERIVE THIS ITSELF, which is the whole reason this crosses the hop.
 * The browser never talks to `api` (D003) — every request there arrives from this tier over
 * the internal network, so the socket's remote address is *ours*. A limiter keyed on it
 * would put the entire internet in one bucket and throttle Michelle. The one place the
 * caller's real address is observable is here, at public ingress, so it is derived here and
 * forwarded on `CLIENT_KEY_HEADER`.
 *
 * `clientIdentifier` takes the entry the PROXY appended rather than the first one in the
 * header — see `lib/rate-limit.ts` for why the leading entry is the caller's to choose, and
 * what reading it cost. It falls back to a shared constant when there is no address at all,
 * which over-throttles rather than silently disabling the limit; the API applies the same
 * rule to a header that arrives missing or malformed.
 */
export async function clientKey(): Promise<string> {
  const headerList = await headers();
  return hashClientId(clientIdentifier(headerList.get("x-forwarded-for")));
}

/**
 * The header the API reads this off.
 *
 * Kept in step with `ClientKey.HeaderName` in
 * `api/src/Practice.Api/RateLimiting/RateLimiting.cs`, and asserted across the two trees by
 * `RateLimitTests.The_bff_forwards_the_key_this_api_partitions_by` — a comment claiming two
 * repositories' worth of code agree is exactly the class of claim that has gone stale here
 * seven times (D072), so the agreement is a test.
 *
 * If the two ever disagree, every request in production lands in the API's shared
 * unattributed bucket: the limiter then throttles the BFF, which means Michelle, and nobody
 * else — silently, and in the direction that looks like the control working.
 */
export const CLIENT_KEY_HEADER = "X-Client-Key";
