import { describe, it, expect } from "vitest";

import {
  ENQUIRY_STATUS_LABELS,
  daysWaiting,
  formatSubmittedAt,
  formatWait,
} from "./enquiry-format";

/**
 * How an enquiry's arrival time is read.
 *
 * The API stores and sends UTC; Michelle reads America/New_York (CLAUDE.md conventions).
 * Every case below is one where those two disagree, because those are the only cases where
 * the conversion can be wrong and nobody would notice: a late-evening enquiry is already
 * tomorrow in UTC, so "waiting 1 day" and "waiting 2 days" differ by which clock you ask.
 *
 * The suite itself runs pinned to America/New_York (vitest.config.mts) — a suite running
 * in UTC agrees with the bug.
 */

describe("formatSubmittedAt", () => {
  /**
   * Control: the `timeZone: PRACTICE_TIME_ZONE` option in formatSubmittedAt.
   * Deleted → **STAYED GREEN**, and that is written down rather than left as a prediction.
   *
   * The suite is pinned to America/New_York (vitest.config.mts, D072), so the ambient zone
   * IS the target zone and no in-process assertion can tell an explicit option from an
   * inherited default. Nothing here can isolate it: changing the process zone mid-run does
   * not reach V8's cached Intl default. What this test does isolate is the FORMAT — the
   * string a clinician reads. What isolates the ZONE is `daysWaiting`'s first case below,
   * which compares a practice-local calendar date against the UTC one and goes red when
   * the conversion is taken away.
   *
   * The option stays because the pin is a property of the test suite and not of the
   * container: a Next server running in UTC would render this in UTC without it.
   */
  it("renders the practice's wall clock, not UTC", () => {
    expect(formatSubmittedAt("2026-08-20T14:30:00Z")).toBe("20 Aug 2026, 10:30 AM");
  });

  /**
   * A timestamp with no zone designator is UTC by contract, and `new Date` would read it
   * as local. The API stamps the Z (D072); this is the second, independent control.
   *
   * Control: the parseApiInstant call in formatSubmittedAt.
   * Replaced with `new Date(value)` → red, "AssertionError: expected
   * '20 Aug 2026, 2:30 PM' to be '20 Aug 2026, 10:30 AM'".
   */
  it("reads a designator-less timestamp as UTC", () => {
    expect(formatSubmittedAt("2026-08-20T14:30:00")).toBe(
      formatSubmittedAt("2026-08-20T14:30:00Z"),
    );
  });
});

describe("daysWaiting", () => {
  /**
   * Counted in CALENDAR DAYS in the practice's zone, not in elapsed hours.
   *
   * "It came in yesterday evening" is what a person means, and 20 elapsed hours can be
   * either 0 or 1 depending on when you start counting.
   *
   * Control: the utcToPracticeDate conversion in daysWaiting.
   * Replaced with the raw ISO date (the UTC calendar day) → red, "AssertionError:
   * expected +0 to be 1" — an 8pm Maryland enquiry is already tomorrow in UTC, so an
   * enquiry that arrived today reads as arriving in the future.
   */
  it("counts practice-local calendar days", () => {
    // 2026-08-20 20:00 ET is 2026-08-21 00:00 UTC.
    expect(daysWaiting("2026-08-21T00:00:00Z", new Date("2026-08-21T15:00:00Z"))).toBe(1);
  });

  it("is 0 for an enquiry that arrived today", () => {
    expect(daysWaiting("2026-08-21T13:00:00Z", new Date("2026-08-21T15:00:00Z"))).toBe(0);
  });

  /**
   * A clock skew must never render as a negative wait.
   *
   * Control: the Math.max(0, …) in daysWaiting.
   * Deleted → red, "AssertionError: expected -2 to be +0".
   */
  it("never counts backwards", () => {
    expect(daysWaiting("2026-08-23T13:00:00Z", new Date("2026-08-21T15:00:00Z"))).toBe(0);
  });
});

describe("formatWait", () => {
  it("says today rather than 0 days", () => {
    expect(formatWait(0)).toBe("Today");
  });

  /**
   * Control: the `days === 1` branch in formatWait.
   * Deleted → red, "AssertionError: expected '1 days' to be '1 day'".
   */
  it("is singular at one day", () => {
    expect(formatWait(1)).toBe("1 day");
  });

  it("is plural beyond that", () => {
    expect(formatWait(12)).toBe("12 days");
  });
});

describe("ENQUIRY_STATUS_LABELS", () => {
  /**
   * Every status the API can send has a word here.
   *
   * A missing key renders the raw enum name, which is the class of defect that puts
   * "OnHold" on a clinician's screen. The four names are fixed and never renumbered
   * (ConsultationStatus), so this list can be exhaustive rather than defensive.
   *
   * Control: the four entries in ENQUIRY_STATUS_LABELS.
   * Any one deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("covers every status the API sends", () => {
    for (const status of ["New", "Contacted", "Converted", "Declined"]) {
      expect(ENQUIRY_STATUS_LABELS[status]).toBeTruthy();
    }
  });
});
