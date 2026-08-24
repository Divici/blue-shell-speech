"use server";

import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { patientsApi } from "@/lib/api/patients";
import { validateNewPatient, type NewPatientInput } from "@/lib/patient-schema";
import type { NewPatientState } from "./state";

/**
 * Creates a patient.
 *
 * Validation runs here, on the server, and again in the domain layer behind the API. The
 * duplication is intentional: this pass produces messages a clinician can act on, and the
 * domain pass is the invariant that cannot be bypassed by any caller.
 *
 * NOTHING FROM THIS FORM IS LOGGED. It carries a child's name, date of birth, and clinical
 * summary — the most sensitive payload in the application.
 */
export async function createPatient(
  _previous: NewPatientState,
  formData: FormData,
): Promise<NewPatientState> {
  const input: NewPatientInput = {
    firstName: String(formData.get("firstName") ?? ""),
    lastName: String(formData.get("lastName") ?? ""),
    dateOfBirth: String(formData.get("dateOfBirth") ?? ""),
    clinicalSummary: String(formData.get("clinicalSummary") ?? ""),
  };

  const { errors, value } = validateNewPatient(input, new Date());

  if (Object.keys(errors).length > 0) {
    return { status: "error", errors, values: input };
  }

  let created;
  try {
    created = await patientsApi.create({
      firstName: value.firstName,
      lastName: value.lastName,
      dateOfBirth: value.dateOfBirth,
      clinicalSummary: value.clinicalSummary,
    });
  } catch {
    return {
      status: "error",
      errors: {},
      values: input,
      message: "We could not save this patient. Please try again.",
    };
  }

  if (!created) {
    return {
      status: "error",
      errors: {},
      values: input,
      message: "We could not save this patient. Please try again.",
    };
  }

  // The caseload list is cached per render; adding a patient must invalidate it.
  revalidatePath("/patients");
  redirect(`/patients/${created.publicId}`);
}
