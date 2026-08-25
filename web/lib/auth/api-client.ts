import "server-only";

import { apiSignal } from "@/lib/api/timeouts";

/**
 * Server-to-server client for the .NET API.
 *
 * The browser never calls the API (D003). This module is the only place that knows the
 * API exists, and it is `server-only` — importing it from a Client Component is a build
 * error rather than a runtime leak of the internal base URL.
 */

function apiBaseUrl(): string {
  const url = process.env.API_BASE_URL;

  if (!url) {
    throw new Error(
      "API_BASE_URL is not configured. The BFF cannot reach the API, and failing here " +
        "is better than rendering a sign-in page that silently cannot authenticate.",
    );
  }

  return url.replace(/\/$/, "");
}

export interface PasswordResponse {
  status: "mfa_required" | "mfa_enrolment_required" | "locked_out" | "invalid";
  userId: string | null;
  lockoutSeconds: number | null;
}

export interface SessionResponse {
  succeeded: boolean;
  userId: string | null;
  providerPublicId: string | null;
  displayName: string | null;
  usedRecoveryCode: boolean;
  recoveryCodesRemaining: number;
}

export interface MfaEnrolment {
  sharedKey: string;
  authenticatorUri: string;
}

export interface MfaEnrolmentResult {
  succeeded: boolean;
  recoveryCodes: string[];
}

async function post<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${apiBaseUrl()}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    // Authentication responses must never be cached, by anything, ever.
    cache: "no-store",
    // The API bounds its own requests and answers 504 past that; this bounds the case
    // where no answer arrives at all. lib/api/timeouts.ts carries the arithmetic.
    signal: apiSignal(),
  });

  if (!response.ok) {
    // The status code is deliberately not surfaced to the caller's UI. An API failure and
    // a rejected credential must look the same to a visitor.
    throw new Error(`API request to ${path} failed with ${response.status}`);
  }

  return (await response.json()) as T;
}

export const authApi = {
  verifyPassword: (email: string, password: string) =>
    post<PasswordResponse>("/auth/password", { email, password }),

  verifyMfa: (userId: string, code: string) =>
    post<SessionResponse>("/auth/mfa/verify", { userId, code }),

  redeemRecoveryCode: (userId: string, code: string) =>
    post<SessionResponse>("/auth/mfa/recovery", { userId, code }),

  beginEnrolment: (userId: string) =>
    post<MfaEnrolment>("/auth/mfa/enrol/begin", { userId }),

  completeEnrolment: (userId: string, code: string) =>
    post<MfaEnrolmentResult>("/auth/mfa/enrol/complete", { userId, code }),
};
