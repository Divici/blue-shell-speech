"use server";

import type { ConsultationState } from "./state";
import {
  validateConsultation,
  isLikelyBot,
  type ConsultationInput,
} from "@/lib/consultation-schema";


/**
 * Handles a consultation request.
 *
 * A Server Action, so validation runs on the server whether or not JavaScript is
 * available. Client-side validation on this form is a convenience; this is the control.
 *
 * NOTHING FROM THIS FORM IS LOGGED. It carries a child's first name and a parent's
 * description of their developmental concerns. Structured logs carry IDs and correlation
 * IDs only (docs/SECURITY.md) — a validation failure is logged as "which fields failed",
 * never as what the parent wrote.
 */
export async function submitConsultation(
  _previous: ConsultationState,
  formData: FormData,
): Promise<ConsultationState> {
  const input: ConsultationInput = {
    parentName: String(formData.get("parentName") ?? ""),
    email: String(formData.get("email") ?? ""),
    phone: String(formData.get("phone") ?? ""),
    childFirstName: String(formData.get("childFirstName") ?? ""),
    childAgeMonths: String(formData.get("childAgeMonths") ?? ""),
    concerns: String(formData.get("concerns") ?? ""),
    preferredContact: String(formData.get("preferredContact") ?? ""),
    website: String(formData.get("website") ?? ""),
  };

  /*
   * Honeypot: report success and store nothing.
   *
   * Telling a bot it was detected teaches whoever wrote it to stop filling the field.
   * A silent accept costs nothing and keeps the trap working.
   */
  if (isLikelyBot(input)) {
    return { status: "success", errors: {} };
  }

  const { errors } = validateConsultation(input);
  if (Object.keys(errors).length > 0) {
    return { status: "error", errors };
  }

  /*
   * TODO(slice 3): POST to the .NET API, which persists a ConsultationRequest and sends
   * a CONTENTLESS notification — "New consultation request, sign in to view". Email is
   * not a channel we control, and a child's name plus a list of developmental concerns
   * sitting in a plaintext inbox is a disclosure (docs/DATA_MODEL.md).
   *
   * Until the API exists, the form validates and confirms but does not persist. That is
   * a deliberate, visible gap rather than a silent one: slice 1 ships the public site,
   * and the API arrives with authentication in slice 2.
   */

  return { status: "success", errors: {} };
}
