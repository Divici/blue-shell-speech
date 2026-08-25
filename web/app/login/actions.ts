"use server";

import { redirect } from "next/navigation";
import { authApi, ApiRateLimitedError } from "@/lib/auth/api-client";
import {
  createPendingMfa,
  createSession,
  destroySession,
  getPendingMfa,
} from "@/lib/auth/session";
import { RATE_LIMITED_MESSAGE, type LoginState, type MfaState } from "./state";

/**
 * Step one: email and password.
 *
 * A success here creates NO session — only a short-lived pending-MFA cookie. There is no
 * branch in this file that produces an authenticated state from a password alone.
 */
export async function signIn(
  _previous: LoginState,
  formData: FormData,
): Promise<LoginState> {
  const email = String(formData.get("email") ?? "").trim();
  const password = String(formData.get("password") ?? "");

  if (!email || !password) {
    return { status: "error", message: "Enter your email and password." };
  }

  let result;
  try {
    result = await authApi.verifyPassword(email, password);
  } catch (error) {
    /*
     * A refusal by the rate limiter is the one failure that gets its own message.
     *
     * Every other one is collapsed below, deliberately. This one is not in that set: the
     * caller already knows they have been sending a lot of requests, because they sent
     * them, so saying "wait" discloses nothing they did not cause — and "that email or
     * password was not recognised" would be an actively wrong thing to tell somebody whose
     * credential was never even looked at.
     *
     * NO NUMBER IN IT. The API sends no `Retry-After` on this path on purpose (D098), so
     * there is nothing to render and nothing an attacker can pace against.
     */
    if (error instanceof ApiRateLimitedError) {
      return {
        status: "error",
        message: RATE_LIMITED_MESSAGE,
      };
    }

    /*
     * An API failure and a rejected credential look identical to the visitor.
     *
     * "The service is unavailable" versus "that was wrong" tells an attacker whether
     * their guess was even evaluated. The distinction is in the server logs, with a
     * correlation ID and no credential.
     */
    return {
      status: "error",
      message: "We could not sign you in. Please try again.",
    };
  }

  switch (result.status) {
    case "mfa_required":
      await createPendingMfa(result.userId!);
      redirect("/login/verify");

    case "mfa_enrolment_required":
      await createPendingMfa(result.userId!);
      redirect("/login/enrol");

    case "locked_out": {
      const minutes = Math.max(1, Math.ceil((result.lockoutSeconds ?? 900) / 60));
      return {
        status: "error",
        message:
          `Too many attempts. Try again in about ${minutes} minute` +
          `${minutes === 1 ? "" : "s"}.`,
      };
    }

    default:
      /*
       * One message for unknown email, wrong password, and disabled account.
       *
       * The API already collapses these, and repeating that here means a future change
       * on either side cannot accidentally turn sign-in into an account-enumeration
       * oracle.
       */
      return { status: "error", message: "That email or password was not recognised." };
  }
}

/** Step two: the authenticator code, or a recovery code. */
export async function verifyMfa(
  _previous: MfaState,
  formData: FormData,
): Promise<MfaState> {
  /*
   * The user id comes from the ENCRYPTED COOKIE, never from the form.
   *
   * A client-supplied userId would let anyone attempt codes against any account — the
   * MFA step would become an oracle for brute-forcing six digits against a victim.
   */
  const pending = await getPendingMfa();
  if (!pending) {
    redirect("/login");
  }

  const code = String(formData.get("code") ?? "").trim();
  const useRecovery = formData.get("mode") === "recovery";

  if (!code) {
    return { status: "error", message: "Enter the code from your authenticator app." };
  }

  let result;
  try {
    result = useRecovery
      ? await authApi.redeemRecoveryCode(pending.userId, code)
      : await authApi.verifyMfa(pending.userId, code);
  } catch (error) {
    // The same branch as step one, for the same reason. `/auth/mfa/verify` and
    // `/auth/mfa/recovery` are both rate limited, so a fix that only covered `signIn`
    // would leave the sibling telling somebody their valid code "was not valid".
    if (error instanceof ApiRateLimitedError) {
      return { status: "error", message: RATE_LIMITED_MESSAGE };
    }

    return { status: "error", message: "We could not verify that code. Please try again." };
  }

  if (!result.succeeded) {
    return {
      status: "error",
      message: useRecovery
        ? "That recovery code was not valid, or has already been used."
        : "That code was not valid. Codes change every 30 seconds — try the current one.",
    };
  }

  await createSession({
    userId: result.userId!,
    providerPublicId: result.providerPublicId!,
    displayName: result.displayName ?? "Provider",
  });

  redirect("/dashboard");
}

/** Enrolment confirmation. Returns the recovery codes, shown exactly once. */
export async function completeEnrolment(
  _previous: MfaState,
  formData: FormData,
): Promise<MfaState> {
  const pending = await getPendingMfa();
  if (!pending) {
    redirect("/login");
  }

  const code = String(formData.get("code") ?? "").trim();
  if (!code) {
    return { status: "error", message: "Enter the six-digit code to confirm setup." };
  }

  let result;
  try {
    result = await authApi.completeEnrolment(pending.userId, code);
  } catch (error) {
    // Third of three. Enrolment confirmation verifies a TOTP as well, so it reaches the
    // same limit — and it is the one credential check the lockout does not count at all.
    if (error instanceof ApiRateLimitedError) {
      return { status: "error", message: RATE_LIMITED_MESSAGE };
    }

    return { status: "error", message: "We could not confirm that code. Please try again." };
  }

  if (!result.succeeded) {
    return {
      status: "error",
      message: "That code was not valid. Check the app and try the current code.",
    };
  }

  /*
   * Recovery codes are returned here and never again — Identity stores them hashed.
   *
   * They are NOT written into the session and NOT persisted anywhere on this side. They
   * exist in this response and in whatever the user does with them.
   */
  return { status: "enrolled", recoveryCodes: result.recoveryCodes };
}

export async function signOut() {
  await destroySession();
  redirect("/login");
}
