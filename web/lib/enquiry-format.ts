import {
  PRACTICE_TIME_ZONE,
  parseApiInstant,
  utcToPracticeDate,
} from "@/lib/practice-time";

/**
 * Reading an enquiry's arrival time.
 *
 * Deliberately NOT `server-only`: the inbox renders on the server, and the detail page's
 * action panel is a Client Component that shows the same wait. One module, one answer —
 * two formatters would disagree on the evening enquiries and nobody would notice.
 *
 * THE PRACTICE'S CLOCK, NEVER THE DEVICE'S AND NEVER UTC. The API stores UTC and Michelle
 * works in America/New_York; between those sits a four- or five-hour offset and two DST
 * boundaries a year. An enquiry that arrived at 8pm in Maryland is already tomorrow in
 * UTC, so "how long has this family been waiting" has two different answers depending on
 * which clock is asked, and only one of them is the one she means.
 */

/** The four states an enquiry can be in, in words rather than enum names. */
export const ENQUIRY_STATUS_LABELS: Record<string, string> = {
  New: "New",
  Contacted: "Contacted",
  Converted: "Became a patient",
  Declined: "Declined",
};

/** "20 Aug 2026, 10:30 AM" — the practice's wall clock at that instant. */
export function formatSubmittedAt(submittedAtUtc: string): string {
  return new Intl.DateTimeFormat("en-GB", {
    timeZone: PRACTICE_TIME_ZONE,
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "numeric",
    minute: "2-digit",
    hour12: true,
  })
    .format(parseApiInstant(submittedAtUtc))
    // en-GB renders "20 Aug 2026 at 10:30 am"; the comma and the capitals are what the
    // rest of this application uses.
    .replace(" at ", ", ")
    .replace(/\b(am|pm)\b/, (meridiem) => meridiem.toUpperCase());
}

/**
 * Whole calendar days between the enquiry arriving and now, in the practice's zone.
 *
 * CALENDAR DAYS, NOT ELAPSED HOURS. "It came in yesterday evening" is what a person means
 * by one day, and twenty elapsed hours is either zero or one depending on where you start
 * counting. Both sides are reduced to a practice-local date first, so the arithmetic
 * happens on the days Michelle would name.
 */
export function daysWaiting(submittedAtUtc: string, now: Date): number {
  const submitted = Date.parse(
    `${utcToPracticeDate(parseApiInstant(submittedAtUtc))}T00:00:00Z`,
  );
  const today = Date.parse(`${utcToPracticeDate(now)}T00:00:00Z`);

  // A clock skew, or an enquiry stamped a moment ahead of this render, must never show as
  // a negative wait — "waiting -1 days" reads as a fault in the page.
  return Math.max(0, Math.round((today - submitted) / 86_400_000));
}

/** "Today", "1 day", "12 days". */
export function formatWait(days: number): string {
  if (days === 0) return "Today";
  if (days === 1) return "1 day";
  return `${days} days`;
}
