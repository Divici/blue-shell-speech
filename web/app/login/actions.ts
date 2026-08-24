"use server";

import { redirect } from "next/navigation";
import { authApi } from "@/lib/auth/api-client";
import {
  createPendingMfa,
  createSession,
  destroySession,
  getPendingMfa,
} from "@/lib/auth/session";
import type { LoginState, MfaState } from "./state";

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
  } catch {
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
  } catch {
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
  } catch {
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
