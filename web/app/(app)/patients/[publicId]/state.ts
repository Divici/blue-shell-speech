import type { GoalErrors, GoalInput } from "@/lib/goal-schema";

/**
 * Goal form and transition state.
 *
 * Kept out of actions.ts: a "use server" module may only export async functions, so any
 * other export is stripped from the client bundle and arrives as `undefined`.
 */
export interface AddGoalState {
  status: "idle" | "added" | "error";
  errors: GoalErrors;
  /** Echoed back on failure so a rejection does not erase what was typed. */
  values?: GoalInput;
  /** A whole-form problem with no single field to attach it to. */
  message?: string;
}

export const INITIAL_ADD_GOAL_STATE: AddGoalState = { status: "idle", errors: {} };

export const EMPTY_GOAL_VALUES: GoalInput = {
  goalText: "",
  domain: "Articulation",
  targetCriteria: "",
  cueLevelExpected: "",
  startDate: "",
  aacModality: "",
  aacDeviceNotes: "",
};

/**
 * Marking a goal met and discontinuing it.
 *
 * There are no fields to attribute a failure to — both submit a pair of identifiers the
 * clinician never typed — so the state is one message and nothing else.
 */
export interface GoalTransitionState {
  status: "idle" | "error";
  message?: string;
}

export const INITIAL_GOAL_TRANSITION_STATE: GoalTransitionState = { status: "idle" };

/**
 * One message for every reason a goal cannot be changed.
 *
 * The API answers 404 for a goal that does not exist AND for one belonging to another
 * provider, byte-identically, so that a status code cannot be used to enumerate real
 * identifiers (D052). Branching on that here would rebuild the oracle in the UI.
 */
export const GOAL_UNAVAILABLE =
  "That goal could not be updated. Refresh the page and try again.";

/** Same reasoning, on the way in: a patient we cannot reach is a patient, full stop. */
export const GOAL_NOT_SAVED =
  "We could not save this goal. Please try again.";
