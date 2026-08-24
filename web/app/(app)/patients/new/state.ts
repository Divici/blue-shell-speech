import type { NewPatientErrors, NewPatientInput } from "@/lib/patient-schema";

/**
 * Kept out of actions.ts: a "use server" module may only export async functions, so any
 * other export is stripped from the client bundle and arrives as `undefined`.
 */
export interface NewPatientState {
  status: "idle" | "error";
  errors: NewPatientErrors;
  /** Echoed back so a validation failure does not erase what was typed. */
  values?: NewPatientInput;
  /** A whole-form problem with no single field to attach it to. */
  message?: string;
}

export const INITIAL_NEW_PATIENT_STATE: NewPatientState = {
  status: "idle",
  errors: {},
};

export const EMPTY_NEW_PATIENT_VALUES: NewPatientInput = {
  firstName: "",
  lastName: "",
  dateOfBirth: "",
  clinicalSummary: "",
};
