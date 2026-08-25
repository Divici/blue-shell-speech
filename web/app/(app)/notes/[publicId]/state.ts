/**
 * Note editor state.
 *
 * Kept out of actions.ts: a "use server" module may only export async functions, so any
 * other export is stripped from the client bundle and arrives as `undefined`.
 */
export interface NoteContent {
  subjective: string;
  objective: string;
  assessment: string;
  plan: string;
}

export interface NoteActionState {
  status: "idle" | "saved" | "signed" | "error";
  message?: string;
  /** Echoed back on failure so nothing the clinician wrote is lost. */
  values?: NoteContent;
}

export const INITIAL_NOTE_STATE: NoteActionState = { status: "idle" };
