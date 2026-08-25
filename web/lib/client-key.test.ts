import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

/**
 * THE HOP, FROM THIS SIDE.
 *
 * The browser never talks to `api` (D003), so the caller's real address is observable only
 * here — at public ingress, on `x-forwarded-for`. Everything the API's login rate limiter
 * knows about who is calling, it knows because this tier derived it and put it on a header.
 * If that stops happening, every request in production lands in the API's shared
 * unattributed bucket and the limiter throttles the BFF, which means Michelle, and nobody
 * else — silently, and in the direction that looks like the control working.
 *
 * The AGREEMENT between the two trees (header name, derivation) is asserted from the API
 * side, in `RateLimitTests.The_bff_forwards_the_key_this_api_partitions_by`, for the reason
 * `RequestBoundsTests` asserts the timeout relationship there: a claim about another tree,
 * written in a comment, is what has gone stale here seven times. What is asserted HERE is
 * behavioural — that the value is what it should be, and that it actually reaches `fetch`.
 *
 * SYNTHETIC DATA ONLY. Every address below is from a reserved documentation range.
 */

vi.mock("server-only", () => ({}));

let forwardedFor: string | null = "198.51.100.9, 203.0.113.7";

vi.mock("next/headers", () => ({
  headers: async () => new Map([["x-forwarded-for", forwardedFor]]),
}));

import { clientKey, CLIENT_KEY_HEADER } from "./client-key";
import { hashClientId, clientIdentifier } from "./rate-limit";

describe("clientKey", () => {
  beforeEach(() => {
    forwardedFor = "198.51.100.9, 203.0.113.7";
  });

  /**
   * ONE DERIVATION, THREE USES — this limiter, `ConsultationRequest.SourceIpHash`, and the
   * API's partition key. A second hashing scheme would produce a value that correlates with
   * nothing either of the others ever recorded, and "did these attempts come from the same
   * place as that enquiry" is the only question any of them exists to answer (D080).
   *
   * Control: the `hashClientId(clientIdentifier(...))` composition in `clientKey`.
   * Falsified by hashing the whole header rather than the identifier — the obvious "simpler"
   * version → red, "AssertionError: expected 'ef0aa9df45b3412e3e2c6740c7f8562c43bf9…' to be
   * 'fec52565aa0cf18f57d7cf5b3ac728503b899…'". Two values that look equally like hashes and
   * index nothing in common.
   */
  it("is the hash of the identity the proxy appended, not the one the caller sent", async () => {
    const expected = await hashClientId(clientIdentifier("198.51.100.9, 203.0.113.7"));

    expect(await clientKey()).toBe(expected);

    // And it is the RIGHT end of the list: rotating the leading entry, which is the half a
    // caller writes, must not move the key. That is the whole of D080.
    forwardedFor = "10.0.0.99, 203.0.113.7";
    expect(await clientKey()).toBe(expected);
  });

  /**
   * A caller with no forwarded address gets the shared bucket, not a fresh one.
   *
   * Over-throttling is the safe direction; a unique fallback would switch the limit off
   * exactly where there is least to go on. It matches what the API does with a header that
   * arrives missing or malformed (`RateLimitPartition.Unattributed`).
   *
   * Control: the `?? "unknown-client"` fallback in `clientIdentifier` (lib/rate-limit.ts).
   * Replaced with a per-call unique value → red, "AssertionError: expected
   * '841784fade1988e5976a4013d65d6230e646d…' to be 'c6d5a5bee9ebaabc917f6c9d53957b7120d7a…'".
   * Two calls, two keys, and a limiter that counts to one forever.
   */
  it("falls back to one shared identity when there is no forwarded address", async () => {
    forwardedFor = null;

    const first = await clientKey();
    const second = await clientKey();

    expect(first).toBe(second);
    expect(first).toBe(await hashClientId(clientIdentifier(null)));
  });

  /**
   * It is a SHA-256 digest in the shape the API accepts.
   *
   * `RateLimitPartition.SourceKey` refuses anything that is not exactly 64 lowercase hex
   * characters and drops it into the shared bucket — which is what stops a caller minting a
   * partition per request out of arbitrary text. A key from this tier that failed that check
   * would be limited, but as part of one global bucket rather than as itself.
   *
   * Control: the `hashClientId` call in `clientKey`.
   * Replaced with the raw identifier → red, "expected '203.0.113.7' to match /^[0-9a-f]{64}$/".
   */
  it("is 64 lowercase hex characters, which is what the API will accept", async () => {
    expect(await clientKey()).toMatch(/^[0-9a-f]{64}$/);
  });

  it("names the header the API reads it off", () => {
    expect(CLIENT_KEY_HEADER).toBe("X-Client-Key");
  });
});

describe("the auth client forwards it", () => {
  let originalFetch: typeof fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    process.env.API_BASE_URL = "http://api.test";
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  function respondWith(status: number, body: unknown): typeof fetch {
    return vi.fn(
      async () =>
        new Response(status === 429 ? "" : JSON.stringify(body), {
          status,
          headers: { "content-type": "application/json" },
        }),
    ) as unknown as typeof fetch;
  }

  /**
   * Control: the `[CLIENT_KEY_HEADER]: await clientKey()` in `lib/auth/api-client.ts`'s
   * `post`.
   * Deleted → red, "TypeError: .toMatch() expects to receive a string, but got undefined",
   * and red on the API side too — `The_bff_forwards_the_key_this_api_partitions_by`:
   * "web/lib/auth/api-client.ts calls fetch 1 time(s) and forwards the client key 0 time(s)."
   * Nothing else notices: the request still succeeds, the sign-in still works, and every
   * login in production quietly shares one bucket with every other.
   */
  it("sends the derived key on every sign-in request", async () => {
    const fetchMock = respondWith(200, { status: "invalid", userId: null });
    globalThis.fetch = fetchMock;

    const { authApi } = await import("./auth/api-client");
    await authApi.verifyPassword("michelle@example.com", "correct-horse-battery-staple");

    const calls = (fetchMock as unknown as ReturnType<typeof vi.fn>).mock.calls;
    const init = (calls[0] as [string, RequestInit])[1];
    const headers = init.headers as Record<string, string>;

    expect(headers[CLIENT_KEY_HEADER]).toMatch(/^[0-9a-f]{64}$/);
    expect(headers[CLIENT_KEY_HEADER]).toBe(await clientKey());
  });

  /**
   * A 429 is its own failure, not the generic one.
   *
   * Every other failure on this path is deliberately collapsed into one message — "the
   * service is unavailable" versus "that was wrong" tells an attacker whether their guess
   * was evaluated. A refusal by the limiter is not in that set: the caller already knows
   * they sent the requests. Telling a real person "that email or password was not
   * recognised" when their credential was never looked at is actively wrong.
   *
   * Control: the `if (response.status === 429) throw new ApiRateLimitedError(...)` in `post`.
   * Deleted, so a 429 falls into the generic `!response.ok` branch → red, "AssertionError:
   * expected Error: API request to /auth/password fail… to be an instance of
   * ApiRateLimitedError".
   */
  it("raises its own error for a refusal, so the UI can say something true", async () => {
    globalThis.fetch = respondWith(429, null);

    const { authApi, ApiRateLimitedError } = await import("./auth/api-client");

    await expect(
      authApi.verifyPassword("michelle@example.com", "correct-horse-battery-staple"),
    ).rejects.toBeInstanceOf(ApiRateLimitedError);
  });
});
