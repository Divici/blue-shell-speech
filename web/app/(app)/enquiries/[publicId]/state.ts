import type { ConversionErrors, ConversionInput } from "@/lib/patient-schema";

/**
 * Inbox action state.
 *
 * Kept out of actions.ts: a "use server" module may only export async functions, so any
 * other export is stripped from the client bundle and arrives as `undefined`.
 */

/**
 * Marking contacted and declining.
 *
 * There are no fields to attribute a failure to — both submit one identifier the clinician
 * never typed — so the state is one message and nothing else.
 */
export interface EnquiryTransitionState {
  status: "idle" | "error";
  message?: string;
}

export const INITIAL_ENQUIRY_TRANSITION_STATE: EnquiryTransitionState = { status: "idle" };

/**
 * One message for every reason an enquiry cannot be moved.
 *
 * The API answers 404 for an enquiry that does not exist AND for one belonging to another
 * provider, byte-identically, so that a status code cannot be used to enumerate real
 * identifiers (D052). Branching on that here would rebuild the oracle in the UI.
 *
 * A REFUSAL IS DIFFERENT and does not come through here: a 409 carries the API's own
 * sentence — the enquiry is already closed, and there is something to do instead — and
 * that wording is surfaced verbatim.
 */
export const ENQUIRY_UNAVAILABLE =
  "That enquiry could not be updated. Refresh the page and try again.";

/**
 * Converting into a patient.
 *
 * A form state rather than a bare message, because two fields are typed and a rejection
 * must not erase them.
 */
export interface ConvertState {
  status: "idle" | "error";
  errors: ConversionErrors;
  /** Echoed back on failure so a rejection does not erase what was typed. */
  values?: ConversionInput;
  /** A whole-form problem with no single field to attach it to. */
  message?: string;
}

export const INITIAL_CONVERT_STATE: ConvertState = { status: "idle", errors: {} };

export const EMPTY_CONVERSION_VALUES: ConversionInput = {
  lastName: "",
  dateOfBirth: "",
};

/** Same reasoning as ENQUIRY_UNAVAILABLE: one message for every reason it did not save. */
export const CONVERSION_FAILED =
  "We could not create this patient record. Please try again.";
