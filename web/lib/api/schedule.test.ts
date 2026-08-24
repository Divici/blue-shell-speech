import { describe, it, expect, vi } from "vitest";

vi.mock("server-only", () => ({}));
vi.mock("@/lib/auth/session", () => ({ getSession: async () => null }));

import { formatVisitTime, formatVisitRange, practiceToday, shiftDate } from "./schedule";

/**
 * Time rendering.
 *
 * Every stored instant is UTC and every displayed one is America/New_York. These assert
 * the conversion directly, including both sides of a DST boundary — the case where a
 * clinician would otherwise drive to a house an hour early.
 */
describe("formatVisitTime", () => {
  it("renders a winter (EST) time in the practice timezone", () => {
    // 2026-01-15 19:00 UTC = 14:00 EST.
    expect(formatVisitTime("2026-01-15T19:00:00Z")).toBe("2:00 PM");
  });

  it("renders a summer (EDT) time in the practice timezone", () => {
    // 2026-06-15 18:00 UTC = 14:00 EDT.
    expect(formatVisitTime("2026-06-15T18:00:00Z")).toBe("2:00 PM");
  });

  /**
   * The same wall-clock appointment is a DIFFERENT UTC instant either side of the clock
   * change. If this ever renders as the same UTC hour, the conversion has been dropped.
   */
  it("maps different UTC instants to the same local time across DST", () => {
    expect(formatVisitTime("2026-01-15T19:00:00Z"))
      .toBe(formatVisitTime("2026-06-15T18:00:00Z"));
  });
});

describe("formatVisitRange", () => {
  it("renders start and end", () => {
    expect(formatVisitRange("2026-06-15T18:00:00Z", 45)).toBe("2:00 PM – 2:45 PM");
  });

  it("handles a range crossing the hour", () => {
    expect(formatVisitRange("2026-06-15T18:30:00Z", 60)).toBe("2:30 PM – 3:30 PM");
  });
});

describe("practiceToday", () => {
  /**
   * The reason the daily view takes a local date.
   *
   * 01:00 UTC is still the previous evening in Maryland. A UTC-based "today" would show
   * the clinician tomorrow's schedule while she is still finishing tonight's visits.
   */
  it("is still the previous day at 01:00 UTC", () => {
    expect(practiceToday(new Date("2026-01-16T01:00:00Z"))).toBe("2026-01-15");
  });

  it("has rolled over by midday UTC", () => {
    expect(practiceToday(new Date("2026-01-16T12:00:00Z"))).toBe("2026-01-16");
  });
});

describe("shiftDate", () => {
  it("moves forward and back", () => {
    expect(shiftDate("2026-06-15", 1)).toBe("2026-06-16");
    expect(shiftDate("2026-06-15", -1)).toBe("2026-06-14");
  });

  it("crosses a month boundary", () => {
    expect(shiftDate("2026-06-30", 1)).toBe("2026-07-01");
  });

  /** Anchored at midday UTC so a DST shift cannot move the date by a day. */
  it("is stable across the spring clock change", () => {
    expect(shiftDate("2026-03-07", 1)).toBe("2026-03-08");
    expect(shiftDate("2026-03-08", 1)).toBe("2026-03-09");
  });
});
