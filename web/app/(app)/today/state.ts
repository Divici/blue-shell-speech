/**
 * Kept out of actions.ts: a "use server" module may only export async functions, so any
 * other export is stripped from the client bundle and arrives as `undefined`.
 */
export interface StartNoteState {
  status: "idle" | "error";
  /**
   * A whole-card problem. There are no fields to attribute an error to — the only input
   * is a hidden visit id the clinician never typed.
   */
  message?: string;
}

export const INITIAL_START_NOTE_STATE: StartNoteState = { status: "idle" };

/**
 * One message for every reason a visit cannot be documented.
 *
 * The API answers 404 for a visit that does not exist AND for one belonging to another
 * provider, byte-identically, so that a status code cannot be used to enumerate real
 * identifiers (D052). Branching on that here would rebuild the oracle in the UI.
 */
export const VISIT_UNAVAILABLE =
  "That visit could not be opened. Refresh the schedule and try again.";
