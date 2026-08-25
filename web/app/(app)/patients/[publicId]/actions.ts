"use server";

import { revalidatePath } from "next/cache";
import { goalsApi, ApiConflictError } from "@/lib/api/notes";
import { validateGoal, type GoalInput } from "@/lib/goal-schema";
import {
  GOAL_NOT_SAVED,
  GOAL_UNAVAILABLE,
  type AddGoalState,
  type GoalTransitionState,
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
