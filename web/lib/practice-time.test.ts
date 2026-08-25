import { describe, it, expect } from "vitest";
import {
  practiceLocalToUtc,
  utcToPracticeDate,
  utcToPracticeTime,
  parseApiInstant,
} from "./practice-time";

/**
 * Wall clock to UTC.
 *
 * The DST cases are the reason this file exists. Michelle books in-home visits across two
 * clock changes a year, and an hour's error means driving to a house where nobody is
 * expecting her.
 */
describe("practiceLocalToUtc", () => {
  it("converts a winter (EST, UTC-5) time", () => {
    expect(practiceLocalToUtc("2026-01-15", "14:00").toISOString()).toBe(
      "2026-01-15T19:00:00.000Z",
    );
  });

  it("converts a summer (EDT, UTC-4) time", () => {
    expect(practiceLocalToUtc("2026-06-15", "14:00").toISOString()).toBe(
      "2026-06-15T18:00:00.000Z",
    );
  });

  /**
   * The same wall-clock appointment is a DIFFERENT UTC instant either side of the change.
   * If these ever match, the conversion has been dropped somewhere.
   */
  it("maps the same wall clock to different instants across DST", () => {
    const winter = practiceLocalToUtc("2026-01-15", "14:00").toISOString();
    const summer = practiceLocalToUtc("2026-06-15", "14:00").toISOString();

    expect(winter).not.toBe(summer);
  });

  /**
   * The two-pass correction earns its keep here.
   *
   * 2026-03-08 is the spring change: clocks jump from 02:00 EST to 03:00 EDT. A 09:00
   * booking that morning is already on the far side of the transition, so the offset at
   * the naive reading is the wrong one.
   */
  it("is correct on the morning of the spring clock change", () => {
    expect(practiceLocalToUtc("2026-03-08", "09:00").toISOString()).toBe(
      "2026-03-08T13:00:00.000Z",
    );
  });

  it("is correct the evening before the spring change", () => {
    expect(practiceLocalToUtc("2026-03-07", "18:00").toISOString()).toBe(
      "2026-03-07T23:00:00.000Z",
    );
  });

  /** 2026-11-01: clocks fall back from 02:00 EDT to 01:00 EST. */
  it("is correct on the morning of the autumn clock change", () => {
    expect(practiceLocalToUtc("2026-11-01", "09:00").toISOString()).toBe(
      "2026-11-01T14:00:00.000Z",
    );
  });

  it("is correct the evening before the autumn change", () => {
    expect(practiceLocalToUtc("2026-10-31", "18:00").toISOString()).toBe(
      "2026-10-31T22:00:00.000Z",
    );
  });

  /**
   * An evening visit is the next UTC day. This is exactly why the daily view slices on a
   * practice-local boundary rather than a UTC one (D057).
   */
  it("puts an evening visit on the next UTC day in winter", () => {
    const utc = practiceLocalToUtc("2026-01-15", "20:00");

    expect(utc.toISOString()).toBe("2026-01-16T01:00:00.000Z");
    // …and reading it back gives the local day the clinician thinks in.
    expect(utcToPracticeDate(utc)).toBe("2026-01-15");
  });

  it("rejects input it cannot read rather than guessing", () => {
    expect(() => practiceLocalToUtc("", "14:00")).toThrow();
    expect(() => practiceLocalToUtc("2026-01-15", "")).toThrow();
  });
});

describe("round-tripping", () => {
  it("returns the original wall clock", () => {
    for (const [date, time] of [
      ["2026-01-15", "14:00"],
      ["2026-06-15", "09:30"],
      ["2026-03-08", "09:00"],
      ["2026-11-01", "16:45"],
      ["2026-01-15", "20:00"],
    ] as const) {
      const utc = practiceLocalToUtc(date, time);

      expect(utcToPracticeDate(utc), `${date} ${time}`).toBe(date);
      expect(utcToPracticeTime(utc), `${date} ${time}`).toBe(time);
    }
  });
});

/**
 * Reading an instant off an API payload.
 *
 * `new Date("2026-06-15T13:00:00")` — no zone designator — is LOCAL time in every
 * JavaScript runtime. Every `*Utc` field this API sends is UTC by contract and by name, so
 * a missing designator is a serialisation defect, not a different reading of the same
 * value, and interpreting it locally silently moves a clinical appointment by the UTC
 * offset. That happened: the schedule endpoint served `startUtc` without a Z, a 9am visit
 * read as "not started yet" until 1pm, and the card that offers to document it showed a
 * sentence explaining why it would not.
 *
 * The API is where that is fixed (PracticeDbContext's UTC value converter, pinned by
 * Every_timestamp_the_api_serialises_is_marked_utc). This is the second, independent
 * control: the reading is right whether or not the designator survives the trip.
 */
describe("parseApiInstant", () => {
  it("reads a timestamp that carries its designator", () => {
    expect(parseApiInstant("2026-06-15T13:00:00Z").toISOString()).toBe(
      "2026-06-15T13:00:00.000Z",
    );
  });

  /** The shape the endpoint actually served. It means the same instant. */
  it("reads a designator-less timestamp as UTC, never as local time", () => {
    expect(parseApiInstant("2026-06-15T13:00:00").toISOString()).toBe(
      "2026-06-15T13:00:00.000Z",
    );
  });

  it("gives the same instant for both spellings", () => {
    expect(parseApiInstant("2026-06-15T13:00:00").getTime()).toBe(
      parseApiInstant("2026-06-15T13:00:00Z").getTime(),
    );
  });

  /** SQL Server's datetime2(3) round-trips milliseconds, so the fractional form arrives too. */
  it("keeps sub-second precision", () => {
    expect(parseApiInstant("2026-06-15T13:00:00.442").toISOString()).toBe(
      "2026-06-15T13:00:00.442Z",
    );
  });

  /** An explicit offset is already unambiguous and must not be re-stamped. */
  it("leaves an explicit offset alone", () => {
    expect(parseApiInstant("2026-06-15T09:00:00-04:00").toISOString()).toBe(
      "2026-06-15T13:00:00.000Z",
    );
  });
});
