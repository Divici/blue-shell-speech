/**
 * How long this tier waits for the API — one number, used by every client.
 *
 * THE TIER THAT GIVES UP FIRST DECIDES THE BOUND. That is the whole reason this file
 * exists as a shared constant rather than a literal per client.
 *
 * So this number has to sit above the API's CEILING — the longest that tier can take
 * before answering. `DatabaseTimeouts.Ceiling` in
 * `api/src/Practice.Infrastructure/Persistence/DatabaseTimeouts.cs` is 11 minutes 50
 * seconds, and it is the sum of two bounds rather than one:
 *
 *   * `DatabaseTimeouts.Request`, 10m20s, which bounds everything that observes a
 *     cancellation token. It is derived from the retry policy underneath — six attempts of
 *     three commands of thirty seconds, plus five backoffs of up to ten — because those
 *     retries are what carry Michelle's first request of the day through an auto-paused
 *     Azure SQL, and a bound below them deletes the recovery.
 *   * `DatabaseTimeouts.UncancellableGrace`, 90s, which bounds the work that deliberately
 *     does not observe one — audit writes, Identity's store calls, and the consultation
 *     notification, none of which can be handed the request's token without either losing
 *     the row when a phone locks or letting a caller skip the lockout counter. ASP.NET
 *     Core's request timeout CANCELS the request and then waits for the pipeline, so such
 *     work runs on past that bound and ADDS to it. The two compose; they do not nest.
 *
 * WHAT THAT SUM DOES NOT COVER, because this comment claimed it covered "everything" and
 * the API's own class docstring said otherwise in the same breath. A transaction's BEGIN
 * and COMMIT are SqlClient round trips rather than commands, so no timeout applies and no
 * token can interrupt one — `AtomicWrites` explains why giving the commit a deadline would
 * make it worse rather than bounded. They are microseconds on a healthy connection and
 * unbounded on a dead one. The margin below is not sized for them; nothing is, and a
 * number nobody has measured would read as a decision and is not one (D072).
 *
 * The earlier version of this comment described only the first of those and called it the
 * ceiling. It was wrong by four minutes in the direction that matters: this constant was
 * 300_000, under the API's real worst case, so the BFF could give up on a request the API
 * went on to answer — which on the consultation form means telling a parent "your enquiry
 * was not stored" about an enquiry that was.
 * `RequestBoundsTests.The_bff_waits_longer_than_this_api_is_prepared_to_spend` reads this
 * file and compares it with the ceiling, and
 * `RequestBoundsTests.The_ceiling_is_the_request_bound_plus_the_uncancellable_tail`
 * measures that the ceiling is real.
 *
 * WHAT IT IS FOR, THEN, IF THE API BOUNDS ITSELF. The case where no answer comes at all: a
 * connection accepted and never answered, a replica torn down mid-request, ingress holding
 * a socket open. Without a signal, `fetch` in Node waits indefinitely and takes a
 * server-action render with it.
 *
 * TWELVE AND A HALF MINUTES IS A LONG SPINNER AND THAT IS A REAL COST, recorded rather
 * than hidden: it is the honest consequence of a retry budget sized for a scale-to-zero
 * database, and every alternative considered is in DECISIONS.md D090. The margin over the
 * ceiling is for the hop itself — Container Apps ingress QUEUES a request while a replica
 * wakes rather than refusing it (docs/PERFORMANCE.md measures a ~22 s cold start), so the
 * time on the wire is not free. Anything upstream of this — ingress, a browser, a load
 * balancer — has bounds of its own that this repository has not measured, and they are
 * deliberately not described here: a number nobody has checked reads as a decision and is
 * not one (D072).
 */
export const API_TIMEOUT_MS = 750_000;

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
 * after the first twelve minutes of the process's life.
 */
export function apiSignal(): AbortSignal {
  return AbortSignal.timeout(API_TIMEOUT_MS);
}
