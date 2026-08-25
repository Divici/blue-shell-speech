/**
 * A small fixed-window rate limiter.
 *
 * `docs/THREAT_MODEL.md` boundary 1 names "rate limit, hashed source IP, honeypot field"
 * as the controls for consultation-form spam and cost amplification. Only the honeypot was
 * implemented; this is the rest.
 *
 * WHY COST AMPLIFICATION MATTERS MORE HERE THAN USUAL: the containers scale to zero, so
 * every submission can wake one. An unthrottled POST loop is a compute bill, not just
 * noise — scale-to-zero makes this app *more* exposed to that threat, not less.
 *
 * KNOWN LIMITATION, STATED RATHER THAN HIDDEN: the counter is in process memory. With
 * `maxReplicas: 3` an attacker gets up to 3x the limit, and the counter resets whenever a
 * replica scales to zero. That is a real weakness and it is not the one that matters here:
 * this exists to stop casual abuse and runaway scripts cheaply. A distributed limiter
 * (Redis, or the API's own middleware) belongs with the rest of the hardening in slice 9,
 * where the traffic actually carries PHI.
 */

export interface RateLimitResult {
  allowed: boolean;
  /** Milliseconds until the caller may try again. 0 when allowed. */
  retryAfterMs: number;
}

interface RateLimiterOptions {
  /** Requests permitted per window. */
  limit: number;
  windowMs: number;
  /** Injectable so tests do not depend on wall-clock time. */
  clock?: () => number;
}

interface Bucket {
  count: number;
  windowStart: number;
}

export class RateLimiter {
  readonly #buckets = new Map<string, Bucket>();
  readonly #limit: number;
  readonly #windowMs: number;
  readonly #clock: () => number;

  constructor({ limit, windowMs, clock = Date.now }: RateLimiterOptions) {
    this.#limit = limit;
    this.#windowMs = windowMs;
    this.#clock = clock;
  }

  /** Number of tracked callers. Exposed so eviction is testable. */
  get size(): number {
    return this.#buckets.size;
  }

  check(key: string): RateLimitResult {
    const now = this.#clock();
    this.#evictExpired(now);

    const bucket = this.#buckets.get(key);

    if (!bucket || now - bucket.windowStart >= this.#windowMs) {
      this.#buckets.set(key, { count: 1, windowStart: now });
      return { allowed: true, retryAfterMs: 0 };
    }

    if (bucket.count < this.#limit) {
      bucket.count += 1;
      return { allowed: true, retryAfterMs: 0 };
    }

    return {
      allowed: false,
      retryAfterMs: bucket.windowStart + this.#windowMs - now,
    };
  }

  /**
   * Drops buckets whose window has passed.
   *
   * Without this the map grows once per unique caller and never shrinks — a slow memory
   * leak that the exact traffic pattern this limiter exists to stop turns into a fast one.
   */
  #evictExpired(now: number): void {
    for (const [key, bucket] of this.#buckets) {
      if (now - bucket.windowStart >= this.#windowMs) {
        this.#buckets.delete(key);
      }
    }
  }
}

/**
 * The one identity in `x-forwarded-for` that the caller could not have chosen.
 *
 * THE HEADER IS A LIST, AND ITS LEFT-HAND END BELONGS TO THE CLIENT. A proxy APPENDS the
 * address it is talking to, so behind Container Apps ingress the header arrives as
 * `whatever-the-browser-sent, real-client-address`. Reading `[0]` — which this used to do —
 * reads the half an attacker writes: rotate it per request and every submission lands in
 * its own bucket, so the limiter counts to one forever for anybody who thinks to set it,
 * and the control it provides is zero. The rightmost entry is the proxy's own observation.
 *
 * TRUSTED EXACTLY AS FAR AS THE TOPOLOGY ALLOWS, which is worth stating plainly: with no
 * proxy in front — a local `next start` — there is nothing appending anything, so the whole
 * header is client-supplied and the limiter is spoofable. That is a property of running
 * without ingress rather than something this function can fix, and it is why the E2E suite
 * can hand each test its own address (`e2e/fixtures.ts`).
 *
 * The fallback is a SHARED constant, so callers we cannot tell apart share one bucket. That
 * over-throttles, which is the safe direction; a unique fallback would disable the limiter
 * precisely where the header is missing.
 */
export function clientIdentifier(forwardedFor: string | null | undefined): string {
  const hops = (forwardedFor ?? "")
    .split(",")
    .map((hop) => hop.trim())
    .filter(Boolean);

  return hops.at(-1) ?? "unknown-client";
}

/**
 * SHA-256 of a client identifier.
 *
 * The limiter needs to tell callers apart; it does not need to know who they are. An IP
 * address is a visitor identifier, and retaining it raw would turn this counter into a log
 * of who visited a paediatric speech-therapy site — which is exactly the kind of
 * incidental data collection `THREAT_MODEL.md` asks us to avoid.
 */
export async function hashClientId(value: string): Promise<string> {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}
