/**
 * How long this tier waits for the API — one number, used by every client.
 *
 * THE TIER THAT GIVES UP FIRST DECIDES THE BOUND. That is the whole reason this file
 * exists as a shared constant rather than a literal per client.
 *
 * The API sets its own ceiling on a request, and derives it from the retry policy
 * underneath: `DatabaseTimeouts.Request` in
 * `api/src/Practice.Infrastructure/Persistence/DatabaseTimeouts.cs` is the worst case the
 * retry policy can produce (six attempts of thirty seconds, plus five backoffs of up to
 * ten) plus one command of grace — 4 minutes 20 seconds. Past that the API answers 504
 * itself, and the answer arrives here as an ordinary failure.
 *
 * So this number sits ABOVE that one. A shorter timeout on this side would not bound the
 * work; it would replace every number on `DatabaseTimeouts` with this one, silently,
 * including the retry budget that exists so Michelle's first request of the day survives
 * an auto-paused Azure SQL. `RequestBoundsTests.The_bff_waits_longer_than_this_api_is_
 * prepared_to_spend` reads this file and fails if either side moves past the other.
 *
 * WHAT IT IS FOR, THEN, IF THE API BOUNDS ITSELF. The case where no answer comes at all:
 * a connection accepted and never answered, a replica torn down mid-request, ingress
 * holding a socket open. Without a signal, `fetch` in Node waits indefinitely and takes a
 * server-action render with it. Five of the six clients here had no signal at all, which
 * is the defect this replaces; the sixth had 25 seconds, which was worse in its own way —
 * it reported "your enquiry was not stored" to a parent whose row the API went on to
 * commit a minute later, on a form whose own comment says there is deliberately no retry
 * because a POST that timed out may well have succeeded.
 *
 * Five minutes, which is that ceiling plus forty seconds. The margin is for the hop
 * itself: Container Apps ingress QUEUES a request while a replica wakes rather than
 * refusing it (docs/PERFORMANCE.md measures a ~22 s cold start), so the time on the wire
 * is not free. Anything upstream of this — ingress, a browser, a load balancer — has
 * bounds of its own that this repository has not measured, and they are deliberately not
 * described here: a number nobody has checked reads as a decision and is not one (D072).
 */
export const API_TIMEOUT_MS = 300_000;

/**
 * A fresh timeout signal for one request.
 *
 * `AbortSignal.timeout` rather than an `AbortController` and a `setTimeout`, because the
 * timer it holds does not keep the process alive and is released when the signal is
 * collected. A hand-rolled version has to be cleared on every success path, and the path
 * somebody forgets is the one that leaks.
 *
 * Called per request, never hoisted to a module-level constant: a signal is single-use and
 * starts counting the moment it is created, so a shared one would abort every request made
 * after the first five minutes of the process's life.
 */
export function apiSignal(): AbortSignal {
  return AbortSignal.timeout(API_TIMEOUT_MS);
}
