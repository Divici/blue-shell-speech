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

/**
 * What a caller sees when the API refuses without evaluating the credential.
 *
 * ONE STRING, USED BY ALL THREE STEPS, because the failure this closes is a fix that lands
 * on the reported call site and misses its siblings (ORCHESTRATION.md's fix-round brief;
 * four consecutive rounds here). Password, TOTP and recovery-code submissions all reach a
 * rate-limited `/auth` route, so all three have to say the same thing.
 *
 * NO DURATION IN IT. The login policies send no `Retry-After` on purpose (D098), so there is
 * no number to render — and a made-up one would be worse than none.
 *
 * Written for a parent-facing practice rather than an operations console: the person most
 * likely to see this is Michelle, on a phone, having mistyped her password.
 */
export const RATE_LIMITED_MESSAGE =
  "Too many attempts from here in the last few minutes. Please wait a little and try again.";

export interface MfaState {
  status: "idle" | "error" | "enrolled";
  message?: string;
  /** Present only immediately after enrolment. Shown once, never retrievable again. */
  recoveryCodes?: string[];
}

export const INITIAL_MFA_STATE: MfaState = { status: "idle" };
