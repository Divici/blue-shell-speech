import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { patientsApi, formatAge, type Guardian } from "@/lib/api/patients";
import { goalsApi } from "@/lib/api/notes";
import { utcToPracticeDate } from "@/lib/practice-time";
import { GoalList } from "./GoalList";
import { AddGoalForm } from "./AddGoalForm";
import { GuardianSection } from "./GuardianSection";
import { AddressSection } from "./AddressSection";

export const metadata: Metadata = {
  title: "Patient",
  robots: { index: false, follow: false },
};

/**
 * A patient record.
 *
 * The API returns 404 both for a record that does not exist and for one belonging to
 * another provider — deliberately indistinguishable. This page renders `notFound()` for
 * either, so the UI cannot leak the difference by showing a different message.
 *
 * The page title is deliberately generic. A browser tab reading "Maya Reyes" is PHI on a
 * screen in a family's living room, and in screen-recording software during a demo.
 */
export default async function PatientPage(props: PageProps<"/patients/[publicId]">) {
  const { publicId } = await props.params;

  /*
   * Both in one round trip. The API answers 404 to the goals list for an unreachable
   * patient exactly as it does for the record itself, so the worst case is an empty array
   * that is never rendered — the notFound() below fires first.
   */
  const [patient, goals] = await Promise.all([
    patientsApi.get(publicId),
    goalsApi.list(publicId),
  ]);

  if (!patient) notFound();

  const primary = patient.guardians.find((g) => g.isPrimaryContact);
  const currentAddress = patient.addresses.find(
    (a) => a.isCurrent && a.addressType === "Session",
  );

  return (
    <>
      <Link href="/patients" className="text-sm font-medium text-blue-deep hover:underline">
        &larr; Patients
      </Link>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <h1 className="font-display text-3xl font-bold text-navy">
          {patient.firstName} {patient.lastName}
        </h1>
        {patient.status !== "Active" && (
          <span className="rounded-full bg-sand/40 px-3 py-1 text-xs font-semibold text-navy">
            {patient.status}
          </span>
        )}
      </div>

      <p className="mt-1 text-ink-muted">
        {formatAge(patient.dateOfBirth)} old
        <span aria-hidden="true"> · </span>
        <span className="sr-only">Date of birth </span>
        {patient.dateOfBirth}
      </p>

      <div className="mt-8 grid gap-6 lg:grid-cols-3">
        <section className="lg:col-span-2 rounded-2xl border border-ice bg-white p-6">
          <h2 className="font-display text-xl font-bold text-navy">Clinical summary</h2>
          {patient.clinicalSummary ? (
            <p className="mt-3 whitespace-pre-wrap leading-relaxed text-ink">
              {patient.clinicalSummary}
            </p>
          ) : (
            <p className="mt-3 text-ink-muted">
              Nothing recorded yet.
            </p>
          )}
        </section>

        {/*
          The at-a-glance panel: who to call, and where to drive.

          It duplicates two facts from the sections further down, and earns it — this is
          what the page is opened for between houses, on a phone, and scrolling past two
          editable sections to find a phone number is not that. The editing lives below;
          this is the reading copy.
        */}
        <aside className="space-y-6">
          <section className="rounded-2xl border border-ice bg-white p-6">
            <h2 className="font-display text-lg font-bold text-navy">Contact</h2>
            {primary ? (
              <GuardianCard guardian={primary} />
            ) : (
              <p className="mt-3 text-sm text-ink-muted">
                No primary contact yet.{" "}
                <a href="#guardians" className="text-blue-deep hover:underline">
                  Add a guardian
                </a>
                .
              </p>
            )}
          </section>

          <section className="rounded-2xl border border-ice bg-white p-6">
            <h2 className="font-display text-lg font-bold text-navy">Session address</h2>
            {currentAddress ? (
              <address className="mt-3 text-sm not-italic leading-relaxed text-ink">
                {currentAddress.line1}
                {currentAddress.line2 && (
                  <>
                    <br />
                    {currentAddress.line2}
                  </>
                )}
                <br />
                {currentAddress.city}, {currentAddress.state} {currentAddress.postalCode}
                {currentAddress.notes && (
                  <span className="mt-2 block text-ink-muted">{currentAddress.notes}</span>
                )}
              </address>
            ) : (
              <p className="mt-3 text-sm text-ink-muted">
                No address on file.{" "}
                <a href="#addresses" className="text-blue-deep hover:underline">
                  Add one
                </a>
                .
              </p>
            )}
          </section>
        </aside>
      </div>

      {/*
        Treatment goals.

        Placed directly under the clinical summary because they are the working part of the
        record: they are what a session is planned against, and what the dictation pipeline
        will classify observations against (presearch §5.4).
      */}
      <section className="mt-6 rounded-2xl border border-ice bg-white p-6">
        <h2 className="font-display text-xl font-bold text-navy">Goals</h2>

        <GoalList patientPublicId={patient.publicId} goals={goals} />

        {/*
          The start date is resolved HERE, on the server, in the practice's zone. Left to
          the browser it would use the device clock, and left to the API it would use the
          UTC date — which after an evening visit is already tomorrow (D057).
        */}
        <AddGoalForm
          patientPublicId={patient.publicId}
          defaultStartDate={utcToPracticeDate(new Date())}
        />
      </section>

      <GuardianSection
        patientPublicId={patient.publicId}
        guardians={patient.guardians}
      />

      {/*
        The move-in date defaults to TODAY IN THE PRACTICE'S ZONE, resolved here on the
        server. Left to the browser it would use the device clock; left to the API it would
        use the UTC date, which after an evening visit is already tomorrow (D057).
      */}
      <AddressSection
        patientPublicId={patient.publicId}
        addresses={patient.addresses}
        defaultEffectiveFrom={utcToPracticeDate(new Date())}
      />
    </>
  );
}

function GuardianCard({ guardian }: { guardian: Guardian }) {
  return (
    <div className="mt-3 text-sm">
      <p className="font-semibold text-navy">
        {guardian.firstName} {guardian.lastName}
      </p>
      <p className="text-ink-muted">{guardian.relationship}</p>

      {guardian.phone && (
        <p className="mt-1">
          <a href={`tel:${guardian.phone.replace(/[^0-9+]/g, "")}`} className="text-blue-deep hover:underline">
            {guardian.phone}
          </a>
        </p>
      )}
      {guardian.email && (
        <p className="break-words">
          <a href={`mailto:${guardian.email}`} className="text-blue-deep hover:underline">
            {guardian.email}
          </a>
        </p>
      )}

      {/*
        Legal authority is shown explicitly, and only when present.
        Releasing a record to a guardian without it is a breach, so this must never be
        something the reader infers from being listed as a contact.
      */}
      {guardian.hasLegalAuthority && (
        <p className="mt-2 inline-block rounded-full bg-teal/15 px-2.5 py-1 text-xs font-semibold text-teal">
          May receive records
        </p>
      )}
    </div>
  );
}
