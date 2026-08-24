import type { Metadata } from "next";
import Link from "next/link";
import { NewPatientForm } from "./NewPatientForm";

export const metadata: Metadata = {
  title: "Add Patient",
  robots: { index: false, follow: false },
};

export default function NewPatientPage() {
  return (
    <>
      <Link href="/patients" className="text-sm font-medium text-blue-deep hover:underline">
        &larr; Patients
      </Link>
      <h1 className="mt-3 font-display text-3xl font-bold text-navy">Add a patient</h1>
      <p className="mt-2 max-w-2xl text-ink-muted">
        Only what the practice actually needs. Guardians and addresses can be added next.
      </p>

      <NewPatientForm />
    </>
  );
}
