import type { ConsultationErrors } from "@/lib/consultation-schema";

/**
 * Form state, kept OUT of actions.ts.
 *
 * A module marked "use server" may only export async functions — every other export is
 * stripped from the client bundle, so importing a constant from it yields `undefined`.
 * That surfaces as a null-dereference during prerendering rather than as a helpful
 * error, so the types and the initial value live here instead.
 */
export interface ConsultationState {
  status: "idle" | "success" | "error";
  errors: ConsultationErrors;
  /** Non-field error, e.g. the API being unreachable. */
  message?: string;
}

export const INITIAL_CONSULTATION_STATE: ConsultationState = {
  status: "idle",
  errors: {},
};
