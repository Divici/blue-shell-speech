import type { DayVisit } from "@/lib/api/schedule";

/**
 * Which visits can be documented, and why the others cannot.
 *
 * A mirror of Appointment.DocumentationBlockedReason in the API, sentence for sentence.
 * The API is the control — it refuses the POST — and this is the reason the schedule can
 * say so before the clinician taps rather than after. Both must answer the same question,
 * the way the AAC fields ask `isAacDomain` in three layers rather than three conditions
 * that drift apart (D062).
 *
 * NOT client-side authorization. Hiding a button is not a rule; the rule is in the API
 * and in the aggregate (CLAUDE.md #6).
 */

/** Enough of a visit to answer the question. Keeps the predicate usable from a test. */
export type DocumentableVisit = Pick<DayVisit, "status" | "startUtc">;

const CANCELLED = "This visit was cancelled. There is nothing to document.";
const NO_SHOW = "This visit was recorded as a no-show. There is nothing to document.";
const NOT_STARTED = "This visit has not started yet. Its note opens when the session does.";

/** The reason a note cannot be started for this visit, or null if one can. */
export function documentationBlockedReason(
  visit: DocumentableVisit,
  now: Date = new Date(),
): string | null {
  if (visit.status === "Cancelled") return CANCELLED;
  if (visit.status === "NoShow") return NO_SHOW;

  // A visit marked complete happened, whatever the clock says — a session closed a couple
  // of minutes early must not refuse its own note for the rest of the hour.
  if (visit.status === "Completed") return null;

  return new Date(visit.startUtc).getTime() > now.getTime() ? NOT_STARTED : null;
}
