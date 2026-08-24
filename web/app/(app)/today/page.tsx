import type { Metadata } from "next";
import Link from "next/link";
import {
  scheduleApi,
  formatVisitRange,
  practiceToday,
  shiftDate,
  type DayVisit,
} from "@/lib/api/schedule";
import { HomeIcon, CalendarIcon } from "@/components/icons";

export const metadata: Metadata = {
  title: "Today",
  robots: { index: false, follow: false },
};

/**
 * The daily visit view (presearch §5.6).
 *
 * Built for a phone in a car, stationary: large touch targets, chronological order, and
 * the address visible without a tap — the clinician needs to know where she is going next
 * without navigating.
 *
 * The date is a LOCAL practice date. A UTC day boundary would move an 8pm visit into
 * tomorrow's list.
 */
export default async function TodayPage(props: PageProps<"/today">) {
  const params = await props.searchParams;
  const date = typeof params.date === "string" ? params.date : practiceToday();

  const schedule = await scheduleApi.day(date);
  const visits = schedule?.visits ?? [];

  return (
    <>
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-display text-3xl font-bold text-navy">
          {formatDayHeading(date)}
        </h1>

        <nav aria-label="Change day" className="ml-auto flex items-center gap-2">
          <Link
            href={`/today?date=${shiftDate(date, -1)}`}
            className="rounded-full border border-ice bg-white px-4 py-2 text-sm font-medium text-ink-muted hover:border-blue"
          >
            <span aria-hidden="true">←</span>
            <span className="sr-only">Previous day</span>
          </Link>
          <Link
            href="/today"
            className="rounded-full border border-ice bg-white px-4 py-2 text-sm font-medium text-blue-deep hover:border-blue"
          >
            Today
          </Link>
          <Link
            href={`/today?date=${shiftDate(date, 1)}`}
            className="rounded-full border border-ice bg-white px-4 py-2 text-sm font-medium text-ink-muted hover:border-blue"
          >
            <span aria-hidden="true">→</span>
            <span className="sr-only">Next day</span>
          </Link>
        </nav>
      </div>

      {visits.length === 0 ? (
        <p className="mt-8 rounded-2xl border border-ice bg-white px-5 py-10 text-center text-ink-muted">
          Nothing scheduled.
        </p>
      ) : (
        <>
          <ol className="mt-8 space-y-4">
            {visits.map((visit) => (
              <VisitCard key={visit.publicId} visit={visit} />
            ))}
          </ol>

          {schedule!.totalMileage > 0 && (
            <p className="mt-6 text-right text-sm text-ink-muted">
              Total mileage:{" "}
              <strong className="text-navy">{schedule!.totalMileage.toFixed(1)} mi</strong>
            </p>
          )}
        </>
      )}
    </>
  );
}

function VisitCard({ visit }: { visit: DayVisit }) {
  const isDone = visit.status === "Completed";
  const isOff = visit.status === "Cancelled" || visit.status === "NoShow";

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
        {visit.patientFirstName} {visit.patientLastName}
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
    </li>
  );
}

/** "Today", "Tomorrow", or a written date — how a person refers to a day. */
function formatDayHeading(date: string): string {
  const today = practiceToday();
  if (date === today) return "Today";
  if (date === shiftDate(today, 1)) return "Tomorrow";
  if (date === shiftDate(today, -1)) return "Yesterday";

  return new Intl.DateTimeFormat("en-US", {
    weekday: "long",
    month: "long",
    day: "numeric",
    timeZone: "UTC",
  }).format(new Date(`${date}T12:00:00Z`));
}
