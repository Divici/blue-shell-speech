import type { Metadata } from "next";
import Link from "next/link";
import { patientsApi, formatAge, type PatientSummary } from "@/lib/api/patients";
import { PatientSearch } from "./PatientSearch";

export const metadata: Metadata = {
  title: "Patients",
  robots: { index: false, follow: false },
};

/**
 * The caseload.
 *
 * A Server Component: the patient list is PHI and is rendered on the server, so it never
 * exists as JSON in a browser bundle or a client-side cache. Only the search box is a
 * Client Component.
 */
export default async function PatientsPage(props: PageProps<"/patients">) {
  const params = await props.searchParams;
  const search = typeof params.q === "string" ? params.q : undefined;
  const includeDischarged = params.discharged === "1";

  const patients = await patientsApi.list(search, includeDischarged);

  return (
    <>
      <div className="flex flex-wrap items-center gap-4">
        <h1 className="font-display text-3xl font-bold text-navy">Patients</h1>
        <Link
          href="/patients/new"
          className="ml-auto rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90"
        >
          Add patient
        </Link>
      </div>

      <div className="mt-6">
        <PatientSearch defaultValue={search ?? ""} includeDischarged={includeDischarged} />
      </div>

      {patients.length === 0 ? (
        <EmptyState search={search} />
      ) : (
        <ul className="mt-6 divide-y divide-ice overflow-hidden rounded-2xl border border-ice bg-white">
          {patients.map((patient) => (
            <PatientRow key={patient.publicId} patient={patient} />
          ))}
        </ul>
      )}
    </>
  );
}

function PatientRow({ patient }: { patient: PatientSummary }) {
  return (
    <li>
      <Link
        href={`/patients/${patient.publicId}`}
        className="flex items-center gap-4 px-5 py-4 transition-colors hover:bg-mist"
      >
        <span className="grid size-10 shrink-0 place-items-center rounded-full bg-ice font-semibold text-blue-deep">
          {patient.firstName.charAt(0)}
          {patient.lastName.charAt(0)}
        </span>

        <span className="min-w-0">
          <span className="block font-semibold text-navy">
            {patient.lastName}, {patient.firstName}
          </span>
          {/* Age in months is the unit early-intervention eligibility uses. */}
          <span className="block text-sm text-ink-muted">
            {formatAge(patient.dateOfBirth)} old
          </span>
        </span>

        {patient.status !== "Active" && (
          <span className="ml-auto rounded-full bg-sand/40 px-3 py-1 text-xs font-semibold text-navy">
            {patient.status}
          </span>
        )}
      </Link>
    </li>
  );
}

/**
 * Empty states are behaviour, not decoration.
 *
 * "No results" and "no patients yet" need different words: the first is a search that
 * missed, the second is a new practice with nothing to show.
 */
function EmptyState({ search }: { search?: string | undefined }) {
  if (search) {
    return (
      <p className="mt-6 rounded-2xl border border-ice bg-white px-5 py-8 text-center text-ink-muted">
        No patients match <strong className="text-navy">{search}</strong>.
      </p>
    );
  }

  return (
    <div className="mt-6 rounded-2xl border border-ice bg-white px-5 py-10 text-center">
      <p className="font-semibold text-navy">No patients yet.</p>
      <p className="mt-1 text-sm text-ink-muted">
        Add the first one to start building the caseload.
      </p>
      <Link
        href="/patients/new"
        className="mt-5 inline-block rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white"
      >
        Add patient
      </Link>
    </div>
  );
}
