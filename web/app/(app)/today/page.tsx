import type { Metadata } from "next";
import Link from "next/link";
import { scheduleApi, practiceToday, shiftDate } from "@/lib/api/schedule";
import { VisitCard } from "./VisitCard";

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

  /*
   * One reading of the clock for the whole page.
   *
   * The cards use it to decide whether a visit has started, and two cards reading it
   * independently could straddle the same minute and disagree about the same visit.
   */
  const now = new Date();

  return (
    <>
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-display text-3xl font-bold text-navy">
          {formatDayHeading(date)}
        </h1>

        <Link
          href={`/appointments/new?date=${date}`}
          className="rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90"
        >
          Schedule a visit
        </Link>

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
        <div className="mt-8 rounded-2xl border border-ice bg-white px-5 py-10 text-center">
          <p className="text-ink-muted">Nothing scheduled.</p>
          <Link
            href={`/appointments/new?date=${date}`}
            className="mt-4 inline-block rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white"
          >
            Schedule a visit
          </Link>
        </div>
      ) : (
        <>
          <ol className="mt-8 space-y-4">
            {visits.map((visit) => (
              <VisitCard key={visit.publicId} visit={visit} now={now} />
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
