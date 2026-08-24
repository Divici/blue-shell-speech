import { describe, it, expect, vi } from "vitest";

vi.mock("server-only", () => ({}));
vi.mock("@/lib/auth/session", () => ({
  getSession: async () => null,
}));

import { ageInMonths, formatAge } from "./patients";

/**
 * Age arithmetic.
 *
 * Worth testing directly because early-intervention eligibility is expressed in months,
 * and an off-by-one around a birthday is invisible in the UI while being wrong in a way
 * that affects whether a child qualifies for a programme.
 */
describe("ageInMonths", () => {
  it("counts whole months elapsed", () => {
    expect(ageInMonths("2024-02-24", new Date("2026-08-24T12:00:00Z"))).toBe(30);
  });

  it("does not count a month until the day is reached", () => {
    expect(ageInMonths("2024-02-24", new Date("2026-08-23T12:00:00Z"))).toBe(29);
  });

  it("is 0 for a newborn", () => {
    expect(ageInMonths("2026-08-24", new Date("2026-08-24T12:00:00Z"))).toBe(0);
  });

  it("never returns a negative age", () => {
    expect(ageInMonths("2026-12-01", new Date("2026-08-24T12:00:00Z"))).toBe(0);
  });

  /** Parsed as UTC so a browser west of Greenwich cannot shift the date by one day. */
  it("is timezone-stable", () => {
    expect(ageInMonths("2024-02-24", new Date("2026-08-24T00:30:00Z"))).toBe(30);
    expect(ageInMonths("2024-02-24", new Date("2026-08-24T23:30:00Z"))).toBe(30);
  });
});

describe("formatAge", () => {
  it("uses months alone under a year", () => {
    expect(formatAge("2026-01-24", new Date("2026-08-24T12:00:00Z"))).toBe("7m");
  });

  it("uses years and months above a year", () => {
    expect(formatAge("2024-02-24", new Date("2026-08-24T12:00:00Z"))).toBe("2y 6m");
  });
});
