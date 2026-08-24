/**
 * Login form state.
 *
 * Kept out of actions.ts: a "use server" module may only export async functions, so a
 * constant exported from there is stripped from the client bundle and arrives as
 * `undefined` (the same trap as the consultation form).
 */
export interface LoginState {
  status: "idle" | "error";
  message?: string;
}

export const INITIAL_LOGIN_STATE: LoginState = { status: "idle" };

export interface MfaState {
  status: "idle" | "error" | "enrolled";
  message?: string;
  /** Present only immediately after enrolment. Shown once, never retrievable again. */
  recoveryCodes?: string[];
}

export const INITIAL_MFA_STATE: MfaState = { status: "idle" };
