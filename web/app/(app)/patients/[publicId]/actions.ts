"use server";

import { revalidatePath } from "next/cache";
import { goalsApi, ApiConflictError } from "@/lib/api/notes";
import { patientsApi } from "@/lib/api/patients";
import { validateGoal, type GoalInput } from "@/lib/goal-schema";
import { validateGuardian, type GuardianInput } from "@/lib/guardian-schema";
import {
  validateAddressCorrection,
  validateNewAddress,
  type AddressCorrectionInput,
  type NewAddressInput,
} from "@/lib/address-schema";
import {
  ADDRESS_NOT_SAVED,
  GOAL_NOT_SAVED,
  GOAL_UNAVAILABLE,
  GUARDIAN_NOT_SAVED,
  type AddGoalState,
  type AddressCorrectionState,
  type AddressFormState,
  type GoalTransitionState,
  type GuardianFormState,
} from "./state";

/**
 * Treatment-goal actions.
 *
 * Server actions rather than client fetches: writing a goal is a mutation against the
 * clinical record, and the browser never talks to the .NET API (D003, CLAUDE.md
 * non-negotiable #6). The provider identity comes from the encrypted session cookie inside
 * goalsApi, so nothing the browser sends can name a different clinician — and the API
 * re-checks ownership regardless, because hiding a form is not authorization.
 *
 * NONE OF THESE LOG. A goal is a clinical statement about a named child.
 *
 * NONE OF THESE REDIRECT, deliberately. Goals are written and closed in a batch while
 * reading down a plan, so the clinician stays on the patient page and the list refreshes
 * underneath her. Were one to navigate, its redirect() would have to sit OUTSIDE the
 * try/catch — redirect signals by throwing, and a catch-all swallows the signal, telling a
 * clinician her write failed when it succeeded (the mistake D061 records).
 */

export async function addGoal(
  _previous: AddGoalState,
  formData: FormData,
): Promise<AddGoalState> {
  const patientPublicId = String(formData.get("patientPublicId") ?? "").trim();

  const input: GoalInput = {
    goalText: String(formData.get("goalText") ?? ""),
    domain: String(formData.get("domain") ?? ""),
    targetCriteria: String(formData.get("targetCriteria") ?? ""),
    cueLevelExpected: String(formData.get("cueLevelExpected") ?? ""),
    startDate: String(formData.get("startDate") ?? ""),
    // Absent from the DOM entirely on a non-AAC domain — the form unmounts them rather
    // than hiding them. Read anyway, because "the UI does not send it" is not a rule.
    aacModality: String(formData.get("aacModality") ?? ""),
    aacDeviceNotes: String(formData.get("aacDeviceNotes") ?? ""),
  };

  if (!patientPublicId) {
    return { status: "error", errors: {}, values: input, message: GOAL_NOT_SAVED };
  }

  const { errors, value } = validateGoal(input);

  if (Object.keys(errors).length > 0) {
    return { status: "error", errors, values: input };
  }

  let created;
  try {
    created = await goalsApi.create(patientPublicId, value);
  } catch {
    return { status: "error", errors: {}, values: input, message: GOAL_NOT_SAVED };
  }

  /*
   * Null is a 404, which the API returns identically for a patient that does not exist and
   * one belonging to someone else (D052). The message distinguishes neither.
   */
  if (!created) {
    return { status: "error", errors: {}, values: input, message: GOAL_NOT_SAVED };
  }

  revalidatePath(`/patients/${patientPublicId}`);

  /*
   * No echo on success.
   *
   * React 19 resets an uncontrolled form after an action, and the form remounts when the
   * echoed `values` identity changes. Returning the saved goal here would refill every
   * field with what was just written, and the next goal would start as a near-duplicate of
   * the last one — which, on a plan of six similar goals, is a real way to record the
   * wrong thing.
   */
  return { status: "added", errors: {} };
}

/**
 * The goal is achieved.
 *
 * A transition, never a delete: a met goal is the record of what therapy accomplished, and
 * "what has this child achieved" is the question families and payers ask.
 */
export async function markGoalMet(
  _previous: GoalTransitionState,
  formData: FormData,
): Promise<GoalTransitionState> {
  return transitionGoal(formData, goalsApi.markMet);
}

/** Stopped without being achieved. Also a transition — the goal stays on the record. */
export async function discontinueGoal(
  _previous: GoalTransitionState,
  formData: FormData,
): Promise<GoalTransitionState> {
  return transitionGoal(formData, goalsApi.discontinue);
}

async function transitionGoal(
  formData: FormData,
  apply: (patientPublicId: string, goalPublicId: string) => Promise<unknown>,
): Promise<GoalTransitionState> {
  const patientPublicId = String(formData.get("patientPublicId") ?? "").trim();
  const goalPublicId = String(formData.get("goalPublicId") ?? "").trim();

  if (!patientPublicId || !goalPublicId) {
    return { status: "error", message: GOAL_UNAVAILABLE };
  }

  let result: unknown;
  try {
    result = await apply(patientPublicId, goalPublicId);
  } catch (error) {
    /*
     * A goal that is already closed. The aggregate refuses to reopen it and the API
     * returns the aggregate's own wording, which explains a rule rather than reporting a
     * malfunction — so it is surfaced, not flattened.
     */
    if (error instanceof ApiConflictError) {
      return { status: "error", message: error.message };
    }
    return { status: "error", message: GOAL_UNAVAILABLE };
  }

  if (!result) {
    return { status: "error", message: GOAL_UNAVAILABLE };
  }

  revalidatePath(`/patients/${patientPublicId}`);
  return { status: "idle" };
}

/**
 * Guardians and addresses.
 *
 * Same rules as the goal actions above: server actions rather than client fetches, the
 * provider identity read from the encrypted session cookie inside patientsApi, nothing
 * logged, nothing navigated. What is new here is that one field carries a legal
 * consequence rather than a clinical one.
 *
 * WHO MAY RECEIVE A CHILD'S RECORDS IS ANSWERED, NEVER ASSUMED. `hasLegalAuthority` is
 * read from its own control and from nothing else — never from the primary-contact
 * checkbox, never defaulted. A radio group with nothing selected sends no field at all,
 * and that reaches the clinician as a question rather than the database as a "no". The
 * column is a bit and cannot hold "nobody said", so this is the last layer where the
 * difference still exists.
 */

/** Present means checked. An unchecked checkbox sends nothing at all. */
function checked(formData: FormData, name: string): boolean {
  return formData.get(name) !== null;
}

function guardianInputFrom(formData: FormData): GuardianInput {
  return {
    firstName: String(formData.get("firstName") ?? ""),
    lastName: String(formData.get("lastName") ?? ""),
    relationship: String(formData.get("relationship") ?? ""),
    phone: String(formData.get("phone") ?? ""),
    email: String(formData.get("email") ?? ""),
    isPrimaryContact: checked(formData, "isPrimaryContact"),
    /*
     * Its own field. Absent when the radio group was never touched, which validateGuardian
     * refuses — the one place "nobody answered" is still distinguishable from "no".
     */
    hasLegalAuthority: String(formData.get("hasLegalAuthority") ?? ""),
  };
}

export async function addGuardian(
  _previous: GuardianFormState,
  formData: FormData,
): Promise<GuardianFormState> {
  const patientPublicId = String(formData.get("patientPublicId") ?? "").trim();
  const input = guardianInputFrom(formData);

  if (!patientPublicId) {
    return { status: "error", errors: {}, values: input, message: GUARDIAN_NOT_SAVED };
  }

  const { errors, value } = validateGuardian(input);

  if (Object.keys(errors).length > 0 || value.hasLegalAuthority === undefined) {
    return { status: "error", errors, values: input };
  }

  let saved;
  try {
    saved = await patientsApi.addGuardian(patientPublicId, {
      ...value,
      hasLegalAuthority: value.hasLegalAuthority,
    });
  } catch {
    return { status: "error", errors: {}, values: input, message: GUARDIAN_NOT_SAVED };
  }

  /*
   * Null is a 404, which the API returns identically for a patient that does not exist and
   * one belonging to someone else (D052). The message distinguishes neither.
   */
  if (!saved) {
    return { status: "error", errors: {}, values: input, message: GUARDIAN_NOT_SAVED };
  }

  revalidatePath(`/patients/${patientPublicId}`);

  /*
   * No echo on success — the same reasoning as addGoal, with a sharper edge.
   *
   * React 19 resets an uncontrolled form after an action and the form remounts when the
   * echoed `values` identity changes, so echoing would refill every field with the
   * guardian just saved. The next guardian would start as a near-duplicate of the last —
   * including the legal-authority answer, which is the one field on this form that must
   * never arrive pre-answered.
   */
  return { status: "saved", errors: {} };
}

export async function updateGuardian(
  _previous: GuardianFormState,
  formData: FormData,
): Promise<GuardianFormState> {
  const patientPublicId = String(formData.get("patientPublicId") ?? "").trim();
  const guardianPublicId = String(formData.get("guardianPublicId") ?? "").trim();
  const input = guardianInputFrom(formData);

  if (!patientPublicId || !guardianPublicId) {
    return { status: "error", errors: {}, values: input, message: GUARDIAN_NOT_SAVED };
  }

  const { errors, value } = validateGuardian(input);

  if (Object.keys(errors).length > 0 || value.hasLegalAuthority === undefined) {
    return { status: "error", errors, values: input };
  }

  let saved;
  try {
    saved = await patientsApi.updateGuardian(patientPublicId, guardianPublicId, {
      ...value,
      hasLegalAuthority: value.hasLegalAuthority,
    });
  } catch {
    return { status: "error", errors: {}, values: input, message: GUARDIAN_NOT_SAVED };
  }

  if (!saved) {
    return { status: "error", errors: {}, values: input, message: GUARDIAN_NOT_SAVED };
  }

  revalidatePath(`/patients/${patientPublicId}`);

  /*
   * No echo here either, and for a different reason: the page re-renders with the saved
   * guardian, and the edit form is keyed on those values (GuardianSection), so it remounts
   * showing what is now on the record. An echo would compete with that and could leave the
   * form displaying an older answer than the card above it.
   */
  return { status: "saved", errors: {} };
}

/**
 * Recording a MOVE.
 *
 * The API closes the current address of the same type and keeps it — a note describing a
 * visit last spring refers to where the family lived then. Not to be used for a typo: see
 * correctAddress.
 */
export async function recordAddress(
  _previous: AddressFormState,
  formData: FormData,
): Promise<AddressFormState> {
  const patientPublicId = String(formData.get("patientPublicId") ?? "").trim();

  const input: NewAddressInput = {
    line1: String(formData.get("line1") ?? ""),
    line2: String(formData.get("line2") ?? ""),
    city: String(formData.get("city") ?? ""),
    state: String(formData.get("state") ?? ""),
    postalCode: String(formData.get("postalCode") ?? ""),
    addressType: String(formData.get("addressType") ?? ""),
    notes: String(formData.get("notes") ?? ""),
    effectiveFrom: String(formData.get("effectiveFrom") ?? ""),
  };

  if (!patientPublicId) {
    return { status: "error", errors: {}, values: input, message: ADDRESS_NOT_SAVED };
  }

  const { errors, value } = validateNewAddress(input);

  if (Object.keys(errors).length > 0) {
    return { status: "error", errors, values: input };
  }

  let saved;
  try {
    saved = await patientsApi.addAddress(patientPublicId, value);
  } catch {
    return { status: "error", errors: {}, values: input, message: ADDRESS_NOT_SAVED };
  }

  if (!saved) {
    return { status: "error", errors: {}, values: input, message: ADDRESS_NOT_SAVED };
  }

  revalidatePath(`/patients/${patientPublicId}`);
  return { status: "saved", errors: {} };
}

/**
 * Fixing a TYPO.
 *
 * One row changes in place. The body carries no address type and no effective date —
 * validateAddressCorrection has nowhere to put them — so this cannot supersede an address
 * or turn a session address into a billing one. The form does not render those fields and
 * this action does not read them, and neither fact is the guard: the shape is.
 */
export async function correctAddress(
  _previous: AddressCorrectionState,
  formData: FormData,
): Promise<AddressCorrectionState> {
  const patientPublicId = String(formData.get("patientPublicId") ?? "").trim();
  const addressPublicId = String(formData.get("addressPublicId") ?? "").trim();

  const input: AddressCorrectionInput = {
    line1: String(formData.get("line1") ?? ""),
    line2: String(formData.get("line2") ?? ""),
    city: String(formData.get("city") ?? ""),
    state: String(formData.get("state") ?? ""),
    postalCode: String(formData.get("postalCode") ?? ""),
    notes: String(formData.get("notes") ?? ""),
  };

  if (!patientPublicId || !addressPublicId) {
    return { status: "error", errors: {}, values: input, message: ADDRESS_NOT_SAVED };
  }

  const { errors, value } = validateAddressCorrection(input);

  if (Object.keys(errors).length > 0) {
    return { status: "error", errors, values: input };
  }

  let saved;
  try {
    saved = await patientsApi.correctAddress(patientPublicId, addressPublicId, value);
  } catch {
    return { status: "error", errors: {}, values: input, message: ADDRESS_NOT_SAVED };
  }

  if (!saved) {
    return { status: "error", errors: {}, values: input, message: ADDRESS_NOT_SAVED };
  }

  revalidatePath(`/patients/${patientPublicId}`);
  return { status: "saved", errors: {} };
}
