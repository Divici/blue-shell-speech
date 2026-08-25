import Link from "next/link";
import { formatVisitRange, type DayVisit } from "@/lib/api/schedule";
import { documentationBlockedReason } from "@/lib/visit-documentation";
import { HomeIcon, CalendarIcon } from "@/components/icons";
import { StartNoteButton } from "./StartNoteButton";

/**
 * One visit on the daily view.
 *
 * Built for a phone in a car, stationary: large touch targets, the patient and the time
 * legible at a glance, and one action per card — document this visit.
 *
 * `now` comes from the page so every card on a schedule agrees about what has started.
 * Two cards reading the clock independently could straddle a visit's start time and
 * disagree about the same minute.
 */
export function VisitCard({ visit, now = new Date() }: { visit: DayVisit; now?: Date }) {
  const isDone = visit.status === "Completed";
  const isOff = visit.status === "Cancelled" || visit.status === "NoShow";
  const patientName = `${visit.patientFirstName} ${visit.patientLastName}`;
  const blocked = documentationBlockedReason(visit, now);

  return (
    <li
      className={`rounded-2xl border bg-white p-5 ${
        isOff ? "border-ice opacity-60" : "border-ice"
      }`}
    >
      <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
        <p className="font-display text-lg font-bold text-navy">
          {formatVisitRange(visit.startUtc, visit.durationMinutes)}
        </p>

        {visit.status !== "Scheduled" && (
          <span
            className={`rounded-full px-3 py-1 text-xs font-semibold ${
              isDone ? "bg-teal/15 text-teal" : "bg-sand/40 text-navy"
            }`}
          >
            {visit.status === "NoShow" ? "No show" : visit.status}
          </span>
        )}

        <span className="ml-auto text-sm text-ink-muted">{visit.appointmentType}</span>
      </div>

      <Link
        href={`/patients/${visit.patientPublicId}`}
        className="mt-2 inline-block text-lg font-semibold text-blue-deep hover:underline"
      >
        {patientName}
      </Link>

      {visit.travelBlockMinutes ? (
        <p className="mt-2 flex items-center gap-2 text-sm text-ink-muted">
          <HomeIcon size={16} />
          Allow {visit.travelBlockMinutes} min travel
        </p>
      ) : null}

      {visit.notes && (
        <p className="mt-2 text-sm leading-relaxed text-ink">{visit.notes}</p>
      )}

      {visit.mileage !== null && (
        <p className="mt-2 flex items-center gap-2 text-sm text-ink-muted">
          <CalendarIcon size={16} />
          {visit.mileage.toFixed(1)} mi
        </p>
      )}

      {/*
        The note entry point.

        Which of the three renders is decided by data already in the day payload, so no
        card makes a request of its own to find out whether it has been documented.

        An existing note wins over the block: a note written before the visit was called
        off is still a clinical record, and the gate removes the entry point, never the
        way back to something already written.
      */}
      <div className="mt-4 border-t border-ice pt-4">
        {visit.notePublicId ? (
          <div className="flex flex-wrap items-center gap-3">
            {/* Named in full — see StartNoteButton for why this is aria-label rather
                than an appended sr-only span. */}
            <Link
              href={`/notes/${visit.notePublicId}`}
              aria-label={`Open note for ${patientName}`}
              className="rounded-full border border-ice bg-white px-5 py-2.5 text-sm font-semibold text-blue-deep hover:border-blue"
            >
              Open note
            </Link>

            {visit.noteStatus && <NoteStatusBadge status={visit.noteStatus} />}
          </div>
        ) : blocked ? (
          <p className="text-sm text-ink-muted">{blocked}</p>
        ) : (
          <StartNoteButton visitPublicId={visit.publicId} patientName={patientName} />
        )}
      </div>
    </li>
  );
}

/**
 * A draft and a signed note must not look alike.
 *
 * "Which of today's notes still need signing" is the question at the end of a day, and it
 * is unanswerable from a schedule where every documented visit reads the same.
 */
function NoteStatusBadge({ status }: { status: string }) {
  const styles =
    status === "Signed"
      ? "bg-teal/15 text-teal"
      : status === "Draft"
        ? "bg-sand/40 text-navy"
        : "bg-ice text-blue-deep";

  return (
    <span className={`rounded-full px-3 py-1 text-xs font-semibold ${styles}`}>{status}</span>
  );
}
