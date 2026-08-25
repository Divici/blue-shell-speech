import type { Metadata } from "next";
import Link from "next/link";
import { patientsApi } from "@/lib/api/patients";
import { practiceToday } from "@/lib/api/schedule";
import { ScheduleVisitForm } from "./ScheduleVisitForm";

export const metadata: Metadata = {
  title: "Schedule a Visit",
  robots: { index: false, follow: false },
};

/**
 * Booking a visit.
 *
 * The patient list is fetched on the SERVER and passed in — so the browser never receives
 * a JSON array of patient names, which would sit in a client cache and in any devtools
 * network log open during a demo.
 */
export default async function NewAppointmentPage(props: PageProps<"/appointments/new">) {
  const params = await props.searchParams;

  // Pre-fill the day the clinician came from, so booking from a schedule view lands there.
  const requested = typeof params.date === "string" ? params.date : undefined;
  const defaultDate = requested ?? practiceToday();

  const patients = await patientsApi.list();

  return (
    <>
      <Link href="/today" className="text-sm font-medium text-blue-deep hover:underline">
        &larr; Schedule
      </Link>

      <h1 className="mt-3 font-display text-3xl font-bold text-navy">Schedule a visit</h1>
      <p className="mt-2 max-w-2xl text-ink-muted">
        Times are in Eastern time. Travel time is counted when checking for clashes.
      </p>

      <ScheduleVisitForm patients={patients} defaultDate={defaultDate} />
    </>
  );
}
