import { describe, it, expect, beforeEach } from "vitest";
import { RateLimiter, hashClientId, clientIdentifier } from "./rate-limit";

describe("RateLimiter", () => {
  let now: number;
  let limiter: RateLimiter;

  beforeEach(() => {
    now = 1_000_000;
    limiter = new RateLimiter({ limit: 3, windowMs: 60_000, clock: () => now });
  });

  it("allows requests up to the limit", () => {
    expect(limiter.check("a").allowed).toBe(true);
    expect(limiter.check("a").allowed).toBe(true);
    expect(limiter.check("a").allowed).toBe(true);
  });

  it("blocks the request after the limit", () => {
    for (let i = 0; i < 3; i++) limiter.check("a");
    expect(limiter.check("a").allowed).toBe(false);
  });

  it("tracks callers independently", () => {
    for (let i = 0; i < 3; i++) limiter.check("a");
    expect(limiter.check("b").allowed).toBe(true);
  });

  it("recovers once the window has passed", () => {
    for (let i = 0; i < 3; i++) limiter.check("a");
    expect(limiter.check("a").allowed).toBe(false);

    now += 60_001;
    expect(limiter.check("a").allowed).toBe(true);
  });

  it("reports how long to wait", () => {
    for (let i = 0; i < 3; i++) limiter.check("a");
    const result = limiter.check("a");
    expect(result.retryAfterMs).toBeGreaterThan(0);
    expect(result.retryAfterMs).toBeLessThanOrEqual(60_000);
  });

  /**
   * Without eviction the map grows once per unique caller, forever — a slow memory leak
   * that a spam run turns into a fast one.
   */
  it("evicts entries whose window has expired", () => {
    for (let i = 0; i < 50; i++) limiter.check(`caller-${i}`);
    expect(limiter.size).toBe(50);

    now += 60_001;
    limiter.check("fresh");
    expect(limiter.size).toBe(1);
  });
});

describe("hashClientId", () => {
  it("is stable for the same input", async () => {
    expect(await hashClientId("203.0.113.7")).toBe(
      await hashClientId("203.0.113.7"),
    );
  });

  it("differs for different inputs", async () => {
    expect(await hashClientId("203.0.113.7")).not.toBe(
      await hashClientId("203.0.113.8"),
    );
  });

  /**
   * THREAT_MODEL boundary 1 specifies a HASHED source IP. An IP address is a visitor
   * identifier; retaining it raw to count requests would mean the rate limiter itself
   * becomes a log of who visited a paediatric therapy site.
   */
  it("does not contain the original address", async () => {
    const hashed = await hashClientId("203.0.113.7");
    expect(hashed).not.toContain("203.0.113.7");
    expect(hashed).toMatch(/^[0-9a-f]{64}$/);
  });
});

/**
 * WHICH ENTRY OF `x-forwarded-for` IS THE CALLER.
 *
 * The header is a LIST, and a caller can put anything they like at the front of it. Behind
 * Container Apps ingress the proxy APPENDS the address it is actually talking to, so the
 * rightmost entry is the one nobody downstream could choose — every entry to its left is
 * whatever the client sent.
 *
 * This used to read `[0]`. That is the entry an attacker controls, so rotating the header
 * gave a fresh bucket on every request and the limiter counted to one, forever, for anybody
 * who thought to set it.
 */
describe("clientIdentifier", () => {
  /**
   * Control: the `hops.at(-1)` in clientIdentifier.
   * Changed to `hops.at(0)` — which is what it used to be — → red,
   * "AssertionError: expected '203.0.113.9' to be '100.64.1.1'".
   */
  it("takes the entry the proxy appended, not the one the caller supplied", () => {
    expect(clientIdentifier("203.0.113.9, 100.64.1.1")).toBe("100.64.1.1");
  });

  /**
   * The consequence, stated as the thing that actually mattered: reading the caller-supplied
   * end meant one caller could mint a fresh bucket per request just by lengthening the header.
   *
   * Control: the `hops.at(-1)` in clientIdentifier.
   * Changed to `hops.at(0)` → red, "AssertionError: expected 3 to be 1".
   */
  it("gives one caller one identity however many hops they claim", () => {
    const forged = [
      "1.1.1.1, 100.64.1.1",
      "2.2.2.2, 3.3.3.3, 100.64.1.1",
      "100.64.1.1",
    ].map(clientIdentifier);

    expect(new Set(forged).size).toBe(1);
  });

  /**
   * NO INDEPENDENT CONTROL, and said so rather than left to look like one.
   *
   * This is the companion to the two above: with one entry, first and last are the same
   * value, so no reachable change to the selection turns it red on its own. It is here to
   * pin that the local, proxy-less case still resolves to an address rather than falling
   * back — which is the case every E2E test runs in.
   */
  it("still reads a single-entry header, which is what a local server sees", () => {
    expect(clientIdentifier("100.64.1.1")).toBe("100.64.1.1");
  });

  /**
   * Control: the `.map((hop) => hop.trim())` in clientIdentifier.
   * Deleted → red, "AssertionError: expected '   100.64.1.1  ' to be '100.64.1.1'" —
   * and, worse than a failing test, two spellings of one caller counting separately.
   */
  it("trims the whitespace the header format allows", () => {
    expect(clientIdentifier("203.0.113.9,   100.64.1.1  ")).toBe("100.64.1.1");
  });

  /**
   * A SHARED bucket rather than none. Failing this way over-throttles callers we cannot
   * tell apart, which is the safe direction; falling back to something unique per request
   * would switch the limiter off exactly where it is needed.
   *
   * Control: the `.filter(Boolean)` in clientIdentifier, which is what leaves `at(-1)`
   * nothing to return when the header holds only separators or nothing at all.
   * Neutered to `.filter(() => true)` → red on all four cases,
   * "AssertionError: expected '' to be 'unknown-client'" — an empty string is a key like
   * any other, so every such caller would still share a bucket, but a nameless one nothing
   * in a log could be traced to.
   */
  it.each([null, "", "   ", ",,"])(
    "falls back to one shared identity when the header says nothing (%s)",
    (header) => {
      expect(clientIdentifier(header)).toBe("unknown-client");
    },
  );
});
