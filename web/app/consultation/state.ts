import type { ConsultationErrors, ConsultationInput } from "@/lib/consultation-schema";

/**
 * Form state, kept OUT of actions.ts.
 *
 * A module marked "use server" may only export async functions — every other export is
 * stripped from the client bundle, so importing a constant from it yields `undefined`.
 * That surfaces as a null-dereference during prerendering rather than as a helpful error,
 * so the types and the initial value live here instead.
 */

/**
 * What the parent typed, echoed back so a validation failure does not erase it.
 *
 * The honeypot is deliberately absent: echoing it back would repopulate the trap and
 * defeat it on the second submission.
 */
export type ConsultationValues = Omit<ConsultationInput, "website">;

export interface ConsultationState {
  status: "idle" | "success" | "error";
  errors: ConsultationErrors;
  /**
   * Previously-submitted values, present only on `error`.
   *
   * Without this, a server-side validation failure re-renders an empty form and the
   * parent loses everything they wrote — including the free-text description of their
   * child, which is the hardest part of the form to type and the whole point of it.
   * Uncontrolled inputs keep their DOM value during a client-side re-render, so this
   * matters most on the no-JavaScript path that Server Actions exist to support.
   */
  values?: ConsultationValues;
  /**
   * A whole-form error with no single field to attach it to — currently the rate limit.
   *
   * This field previously existed with no writer and no renderer. It now has both; if a
   * future state carries a message, the form must render it, or the parent sees a form
   * that silently refuses to submit.
   */
  message?: string;
}

export const INITIAL_CONSULTATION_STATE: ConsultationState = {
  status: "idle",
  errors: {},
};

/** Blank values, so the form can read `state.values` unconditionally. */
export const EMPTY_CONSULTATION_VALUES: ConsultationValues = {
  parentName: "",
  email: "",
  phone: "",
  childFirstName: "",
  childAgeMonths: "",
  concerns: "",
  preferredContact: "Either",
};
