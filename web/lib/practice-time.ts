/**
 * AT A GLANCE
 * -----------
 * Turns "2:00 PM on 8 March" into the exact moment in time the database stores, and back.
 *
 * The database keeps everything in UTC, which never shifts. People think in local time,
 * which shifts twice a year for daylight saving. Converting between them is not a fixed
 * offset — the offset depends on the very moment you are trying to work out, which is why
 * the function below has to make two passes at it.
 *
 * Get this wrong and Michelle drives to a family's house an hour early.
 */

/**
 * Converting between the practice's wall clock and UTC.
 *
 * Michelle types "2:00 PM on 8 March". The database stores UTC. Between those two facts
 * sits a DST boundary twice a year, and getting this wrong means she drives to a house an
 * hour early or an hour late.
 *
 * Deliberately hand-rolled rather than pulling in a date library: the whole problem is one
 * function, and the correctness argument is easier to read than a dependency's docs.
 */

export const PRACTICE_TIME_ZONE = "America/New_York";

/**
 * The practice zone's UTC offset, in minutes, at a given instant.
 *
 * Derived by formatting the instant in the target zone and comparing it back to UTC —
 * which is the only approach that stays correct when the offset rules change, because it
 * asks the platform's own timezone database rather than hardcoding -5 or -4.
 */
function offsetMinutesAt(instant: Date): number {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: PRACTICE_TIME_ZONE,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).formatToParts(instant);

  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value ?? "0");

  // The same wall-clock reading, interpreted as if it were UTC.
  const asIfUtc = Date.UTC(
    get("year"),
    get("month") - 1,
    get("day"),
    // Intl renders midnight as hour 24 in some locales under hour12:false.
    get("hour") % 24,
    get("minute"),
    get("second"),
  );

  return (asIfUtc - instant.getTime()) / 60_000;
}

/**
 * Converts a practice-local date and time to a UTC instant.
 *
 * @param date "yyyy-mm-dd" as an `<input type="date">` produces
 * @param time "HH:mm" as an `<input type="time">` produces
 *
 * Two passes, and the second one matters. The offset depends on the instant, and the
 * instant is what we are solving for — so the first pass guesses using the offset at the
 * naive UTC reading, and the second corrects using the offset at the candidate instant.
 * Without it, a time within a few hours of a DST transition lands an hour out.
 */
export function practiceLocalToUtc(date: string, time: string): Date {
  const [year, month, day] = date.split("-").map(Number);
  const [hour, minute] = time.split(":").map(Number);

  if (!year || !month || !day || hour === undefined || minute === undefined) {
    throw new Error(`Could not read "${date} ${time}" as a date and time.`);
  }

  const naive = Date.UTC(year, month - 1, day, hour, minute);

  const firstGuess = new Date(naive - offsetMinutesAt(new Date(naive)) * 60_000);
  const corrected = new Date(naive - offsetMinutesAt(firstGuess) * 60_000);

  return corrected;
}

/**
 * Reads a `*Utc` timestamp from an API payload.
 *
 * Every timestamp this API sends is UTC — by contract, and by the name of the field it
 * arrives in. `new Date(value)` does not know that: given a value with no zone designator
 * it applies the LOCAL zone, which in Maryland moves a clinical appointment four or five
 * hours. That is not a rounding error on a schedule read between houses; it is the
 * difference between a card that offers to document the session happening in the room and
 * one that says the visit has not started.
 *
 * The endpoint is where this is fixed — PracticeDbContext stamps DateTimeKind.Utc on every
 * value read out of `datetime2`, and an integration test asserts every `*Utc` field on the
 * wire ends in Z. This is the second, independent control, on the D034 argument that two
 * checks looking at the problem from different sides catch what either alone misses. It is
 * recovery, not a substitute: if the designator goes missing again the API suite is what
 * says so, and this is what stops a clinician seeing the wrong hour meanwhile.
 *
 * Only a bare `yyyy-mm-ddThh:mm:ss[.fff]` is stamped. A value carrying `Z` or an explicit
 * offset already says what it means and is passed through untouched.
 */
export function parseApiInstant(value: string): Date {
  const hasDesignator = /(?:Z|[+-]\d\d:?\d\d)$/.test(value);
  return new Date(hasDesignator ? value : `${value}Z`);
}

/** The practice-local date ("yyyy-mm-dd") for a UTC instant. */
export function utcToPracticeDate(instant: Date): string {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: PRACTICE_TIME_ZONE,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(instant);
}

/** The practice-local time ("HH:mm") for a UTC instant. */
export function utcToPracticeTime(instant: Date): string {
  return new Intl.DateTimeFormat("en-GB", {
    timeZone: PRACTICE_TIME_ZONE,
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).format(instant);
}

/**
 * Durations Michelle actually books.
 *
 * A free-text minutes field invites a typo that the domain would reject at 241 minutes
 * but happily accept at 6 — and a six-minute therapy session is a data-entry error the
 * database cannot distinguish from a real one.
 */
export const DURATION_OPTIONS = [30, 45, 60, 90] as const;

/** Travel allowances. Counted when detecting scheduling conflicts (D056). */
export const TRAVEL_OPTIONS = [0, 15, 30, 45, 60] as const;
