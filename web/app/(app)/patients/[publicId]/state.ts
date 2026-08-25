import type { GoalErrors, GoalInput } from "@/lib/goal-schema";
import type { GuardianErrors, GuardianInput } from "@/lib/guardian-schema";
import type {
  AddressCorrectionErrors,
  AddressCorrectionInput,
  NewAddressErrors,
  NewAddressInput,
} from "@/lib/address-schema";

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

/* ------------------------------------------------------------- guardians */

/**
 * Adding and editing a guardian share one state shape, because they are the same form
 * asking the same questions of the same aggregate. What differs is which action it posts
 * to and whether a guardian id travels with it.
 */
export interface GuardianFormState {
  status: "idle" | "saved" | "error";
  errors: GuardianErrors;
  /** Echoed back on failure so a rejection does not erase what was typed. */
  values?: GuardianInput;
  /** A whole-form problem with no single field to attach it to. */
  message?: string;
}

export const INITIAL_GUARDIAN_STATE: GuardianFormState = { status: "idle", errors: {} };

/**
 * A blank guardian.
 *
 * `hasLegalAuthority` is the EMPTY STRING, not "no". Neither radio starts selected, so the
 * form cannot be submitted with an answer nobody gave — and a saved "no" always means
 * somebody chose it. See LegalAuthorityAnswer.
 */
export const EMPTY_GUARDIAN_VALUES: GuardianInput = {
  firstName: "",
  lastName: "",
  relationship: "",
  phone: "",
  email: "",
  isPrimaryContact: false,
  hasLegalAuthority: "",
};

/** Same reasoning as GOAL_NOT_SAVED: one message for every reason it did not save. */
export const GUARDIAN_NOT_SAVED =
  "We could not save this guardian. Please try again.";

/* ------------------------------------------------------------- addresses */

/** Recording a move: a new row, and the previous one of that type is closed. */
export interface AddressFormState {
  status: "idle" | "saved" | "error";
  errors: NewAddressErrors;
  values?: NewAddressInput;
  message?: string;
}

export const INITIAL_ADDRESS_STATE: AddressFormState = { status: "idle", errors: {} };

export const EMPTY_ADDRESS_VALUES: NewAddressInput = {
  line1: "",
  line2: "",
  city: "",
  state: "MD",
  postalCode: "",
  addressType: "Session",
  notes: "",
  effectiveFrom: "",
};

/**
 * Fixing a typo: one row changes in place.
 *
 * A separate state type rather than a flag on the one above, mirroring the two API calls
 * and the two aggregate methods. A correction has no address type and no dates to carry,
 * and a shape that could carry them is a shape something could smuggle them through.
 */
export interface AddressCorrectionState {
  status: "idle" | "saved" | "error";
  errors: AddressCorrectionErrors;
  values?: AddressCorrectionInput;
  message?: string;
}

export const INITIAL_ADDRESS_CORRECTION_STATE: AddressCorrectionState = {
  status: "idle",
  errors: {},
};

export const ADDRESS_NOT_SAVED =
  "We could not save this address. Please try again.";
