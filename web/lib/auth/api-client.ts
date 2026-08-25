import "server-only";

import { apiSignal } from "@/lib/api/timeouts";
import { clientKey, CLIENT_KEY_HEADER } from "@/lib/client-key";

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

/**
 * The API refused this request without evaluating it.
 *
 * ITS OWN TYPE BECAUSE THE UI HAS TO SAY SOMETHING DIFFERENT. Every other failure on this
 * path is collapsed into one message on purpose — "the service is unavailable" versus "that
 * was wrong" tells an attacker whether their guess was even evaluated. A 429 is not in that
 * set: the caller already knows they are sending too many requests, because they sent them.
 * Telling them to wait is the only answer that helps a real person who has fumbled their
 * password on a phone, and it discloses nothing they did not cause.
 *
 * IT CARRIES NO DURATION, and that is the API's decision rather than an omission here. The
 * login policies deliberately send no `Retry-After` — see `RateLimitPolicy` — so there is
 * no number to render and no number to pace an attacker with (D098).
 */
export class ApiRateLimitedError extends Error {}

async function post<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${apiBaseUrl()}${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      /*
       * THE ONE THING THIS TIER KNOWS THAT THE API CANNOT WORK OUT.
       *
       * The browser never talks to `api`, so every request arrives there from this
       * process: a limiter keyed on the socket's address would put the whole internet in
       * one bucket and throttle Michelle. The caller's real address is observable only
       * here, at public ingress, so it is derived here — by the same function that keys
       * the consultation limiter and fills `ConsultationRequest.SourceIpHash` — and
       * forwarded. See `lib/client-key.ts`.
       */
      [CLIENT_KEY_HEADER]: await clientKey(),
    },
    body: JSON.stringify(body),
    // Authentication responses must never be cached, by anything, ever.
    cache: "no-store",
    // The API bounds its own requests and answers 504 past that; this bounds the case
    // where no answer arrives at all. lib/api/timeouts.ts carries the arithmetic.
    signal: apiSignal(),
  });

  if (response.status === 429) {
    throw new ApiRateLimitedError(`API request to ${path} was rate limited`);
  }

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
