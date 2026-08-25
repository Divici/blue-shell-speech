"use server";

import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { enquiriesApi, type EnquirySummary } from "@/lib/api/enquiries";
import { ApiConflictError } from "@/lib/api/errors";
import { validateConversion, type ConversionInput } from "@/lib/patient-schema";
import {
  CONVERSION_FAILED,
  ENQUIRY_UNAVAILABLE,
  type ConvertState,
  type EnquiryTransitionState,
} from "./state";

/**
 * Moving an enquiry through the inbox.
 *
 * Server actions rather than client fetches: the browser never talks to the .NET API
 * (D003, CLAUDE.md non-negotiable #6). The provider identity comes from the encrypted
 * session cookie inside enquiriesApi, so nothing the browser sends can name a different
 * clinician — and the API re-checks ownership regardless, because hiding a button is not
 * authorization.
 *
 * NONE OF THESE LOG. An enquiry holds a child's first name and a parent's description of
 * that child's difficulties (non-negotiable #3).
 *
 * NOTHING HERE DELETES. There is no endpoint that removes an enquiry: declining is a
 * transition, and the row stays exactly as the parent wrote it.
 */

export async function markContacted(
  _previous: EnquiryTransitionState,
  formData: FormData,
): Promise<EnquiryTransitionState> {
  return transitionEnquiry(formData, enquiriesApi.markContacted);
}

export async function declineEnquiry(
  _previous: EnquiryTransitionState,
  formData: FormData,
): Promise<EnquiryTransitionState> {
  return transitionEnquiry(formData, enquiriesApi.decline);
}

/**
 * The shared half of the two status moves.
 *
 * NEITHER NAVIGATES, deliberately. Michelle rings a family and marks the enquiry contacted
 * from the same screen she is reading the number off; sending her elsewhere would take the
 * phone number away mid-call. Were one to navigate, its redirect() would have to sit
 * OUTSIDE the try/catch — redirect signals by throwing, and a catch-all swallows the
 * signal, telling a clinician her write failed when it succeeded (the mistake D061
 * records, and the reason convertToPatient below is shaped the way it is).
 */
async function transitionEnquiry(
  formData: FormData,
  apply: (publicId: string) => Promise<EnquirySummary | null>,
): Promise<EnquiryTransitionState> {
  const publicId = String(formData.get("publicId") ?? "").trim();

  if (!publicId) {
    return { status: "error", message: ENQUIRY_UNAVAILABLE };
  }

  let moved: EnquirySummary | null;
  try {
    moved = await apply(publicId);
  } catch (error) {
    /*
     * A closed enquiry. The API refuses it and says what to do instead — open the patient
     * record, or leave a declined enquiry as it stands — so the sentence is surfaced
     * rather than flattened into "please try again".
     *
     * Safe to surface: the API only reaches that check on an enquiry this provider can
     * already see. Anything belonging to somebody else is a 404 well before it (D052).
     */
    if (error instanceof ApiConflictError) {
      return { status: "error", message: error.message };
    }
    return { status: "error", message: ENQUIRY_UNAVAILABLE };
  }

  /*
   * Null is a 404, which the API returns identically for an enquiry that does not exist
   * and one belonging to someone else (D052). The message distinguishes neither.
   */
  if (!moved) {
    return { status: "error", message: ENQUIRY_UNAVAILABLE };
  }

  // Both views: the inbox shows the status badge, the detail page shows the actions that
  // are still available.
  revalidatePath("/enquiries");
  revalidatePath(`/enquiries/${publicId}`);

  return { status: "idle" };
}

/**
 * The enquiry becomes a patient, and the clinician lands on the child's record.
 *
 * THE CHILD'S FIRST NAME IS NOT SENT. The API takes it off the enquiry row; a first name
 * in this request would be a value the browser chose for a field the enquiry already
 * answers. The surname and the date of birth ARE typed, because the public form never
 * collected them — and the date is not derived from the age the parent gave (see
 * validateConversion).
 */
export async function convertToPatient(
  _previous: ConvertState,
  formData: FormData,
): Promise<ConvertState> {
  const publicId = String(formData.get("publicId") ?? "").trim();

  const input: ConversionInput = {
    lastName: String(formData.get("lastName") ?? ""),
    dateOfBirth: String(formData.get("dateOfBirth") ?? ""),
  };

  if (!publicId) {
    return { status: "error", errors: {}, values: input, message: CONVERSION_FAILED };
  }

  /*
   * Validated here and again in the domain layer behind the API. The duplication is
   * intentional and one-directional: this pass produces messages a clinician can act on,
   * and Patient.Create is the invariant no caller can bypass.
   */
  const { errors, value } = validateConversion(input, new Date());

  if (Object.keys(errors).length > 0) {
    return { status: "error", errors, values: input };
  }

  let converted: EnquirySummary | null;
  try {
    converted = await enquiriesApi.convert(publicId, value);
  } catch (error) {
    if (error instanceof ApiConflictError) {
      return { status: "error", errors: {}, values: input, message: error.message };
    }
    return { status: "error", errors: {}, values: input, message: CONVERSION_FAILED };
  }

  /*
   * Null is a 404 (D052). A summary with no patient on it is stranger and worse: the API
   * writes the link and the status in one transaction, so a converted enquiry always names
   * its patient — and announcing one it did not name would send a clinician to
   * `/patients/undefined` at best, and tell her a child's record exists at worst.
   */
  if (!converted?.convertedPatientPublicId) {
    return { status: "error", errors: {}, values: input, message: CONVERSION_FAILED };
  }

  // The caseload list is cached per render; a new patient must invalidate it, and the
  // enquiry now reads "became a patient" in both inbox views.
  revalidatePath("/patients");
  revalidatePath("/enquiries");
  revalidatePath(`/enquiries/${publicId}`);

  /*
   * OUTSIDE the try/catch above, deliberately.
   *
   * redirect() signals by throwing. Called inside a catch-all it would be caught as a
   * failure, and a clinician whose patient record was created perfectly well would be told
   * it was not — so she would tap again, and the second attempt is refused because the
   * enquiry is already converted. She would then be looking at an error about a child
   * whose record exists.
   */
  redirect(`/patients/${converted.convertedPatientPublicId}`);
}
