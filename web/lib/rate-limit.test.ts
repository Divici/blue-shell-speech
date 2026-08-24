import { describe, it, expect, beforeEach } from "vitest";
import { RateLimiter, hashClientId } from "./rate-limit";

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
    expect(await hashClientId("203.0.113.7")).toBe(await hashClientId("203.0.113.7"));
  });

  it("differs for different inputs", async () => {
    expect(await hashClientId("203.0.113.7")).not.toBe(await hashClientId("203.0.113.8"));
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
