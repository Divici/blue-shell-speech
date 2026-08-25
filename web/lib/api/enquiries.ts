import "server-only";

import { getSession } from "@/lib/auth/session";
import { apiSignal } from "@/lib/api/timeouts";
import { ApiConflictError } from "@/lib/api/errors";
import type { ConversionInput } from "@/lib/patient-schema";

/**
 * The consultation inbox, server to server.
 *
 * `consultations.ts` is this file's counterpart on the other side of the login: it posts a
 * parent's enquiry with no session, because the person filling the form has no account.
 * Everything here is read and written by Michelle, so every call forwards the provider
 * identity read from the ENCRYPTED SESSION COOKIE — the caller never supplies one, and
 * there is no parameter it could supply one through.
 *
 * `server-only`: importing this from a Client Component is a build error rather than a
 * runtime leak of the internal API URL, or of a family's enquiry into a browser bundle.
 */

/** One row of the inbox. Deliberately carries no `concerns` — see ConsultationRequestSummary. */
export interface EnquirySummary {
  publicId: string;
  parentName: string;
  childFirstName: string;
  childAgeMonths: number;
  preferredContactMethod: "Email" | "Phone" | "Either";
  status: "New" | "Contacted" | "Converted" | "Declined";
  /** UTC, always with a Z. Rendered in America/New_York (`lib/enquiry-format.ts`). */
  submittedAtUtc: string;
  /** The child's record, once this enquiry became one. Opaque, never a row id. */
  convertedPatientPublicId: string | null;
}

/** The whole enquiry, including what the parent wrote. The API audits every read of it. */
export interface EnquiryDetail extends EnquirySummary {
  email: string;
  phone: string | null;
  concerns: string;
}

function apiBaseUrl(): string {
  const url = process.env.API_BASE_URL;
  if (!url) throw new Error("API_BASE_URL is not configured.");
  return url.replace(/\/$/, "");
}

async function request<T>(path: string, init?: RequestInit): Promise<T | null> {
  const session = await getSession();

  /*
   * REFUSED BEFORE THE NETWORK, not left to the API.
   *
   * The route group already redirects an unauthenticated visitor, and hiding a page is not
   * authorization (CLAUDE.md non-negotiable #6). Without this, an unidentified request
   * reaches an API whose query filter matches nothing for a null provider — and an empty
   * inbox renders as though the practice had no enquiries.
   */
  if (!session) throw new Error("No provider session.");

  const response = await fetch(`${apiBaseUrl()}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      // Read from the session cookie, never from the request.
      "X-Provider-Id": session.providerPublicId,
      ...init?.headers,
    },
    // A family's enquiry must never sit in a cache, at any layer.
    cache: "no-store",
    // The API bounds its own requests and answers 504 past that; this bounds the case
    // where no answer arrives at all. lib/api/timeouts.ts carries the arithmetic.
    signal: apiSignal(),
  });

  /*
   * 404 becomes null, not an exception.
   *
   * The API answers 404 for an enquiry belonging to another provider exactly as it does
   * for one that does not exist — deliberately, so the two are indistinguishable (D052).
   * Treating them identically here preserves that: the UI cannot render a different
   * message for "forbidden", because it is never told.
   */
  if (response.status === 404) return null;

  /*
   * 409 is a rule the clinician needs to read, not a malfunction. The enquiry is already
   * closed, and the sentence says what to do instead.
   */
  if (response.status === 409) {
    const body = (await response.json().catch(() => null)) as { message?: string } | null;
    throw new ApiConflictError(body?.message ?? "That is not allowed in the current state.");
  }

  if (!response.ok) throw new Error(`Enquiries API ${path} failed with ${response.status}`);
  if (response.status === 204) return null;

  return (await response.json()) as T;
}

export const enquiriesApi = {
  /**
   * The inbox. Unanswered first, newest first within a status — the API decides the order,
   * because the index it reads was built for exactly that query.
   */
  list: (status?: string) =>
    request<EnquirySummary[]>(
      `/consultation-requests${status ? `?status=${encodeURIComponent(status)}` : ""}`,
    ).then((result) => result ?? []),

  /** One enquiry, in full. This read is audited on the API side (D065). */
  get: (publicId: string) =>
    request<EnquiryDetail>(`/consultation-requests/${publicId}`),

  /*
   * THE THREE MOVES, AND NO DELETE.
   *
   * There is no endpoint that removes an enquiry, so there is nothing here to call. A
   * declined enquiry stays exactly as the parent wrote it: "who did we turn away, and
   * when" is a question about the practice, and a deleted row answers it with silence.
   *
   * Each is TYPED, so a 404 is distinguishable from a success. `request` maps 404 to null,
   * and an untyped result would leave an enquiry belonging to somebody else looking
   * identical to one closed perfectly well — with the UI reporting a write that never
   * happened.
   */
  markContacted: (publicId: string) =>
    request<EnquirySummary>(`/consultation-requests/${publicId}/contacted`, {
      method: "POST",
    }),

  decline: (publicId: string) =>
    request<EnquirySummary>(`/consultation-requests/${publicId}/declined`, {
      method: "POST",
    }),

  /**
   * Becomes a patient, in one transaction on the API side.
   *
   * The child's first name is NOT sent: the API takes it off the enquiry row. A first name
   * in this body would be a value the browser chose for a field the enquiry already
   * answers, and the point of converting is that nothing is retyped.
   */
  convert: (publicId: string, body: ConversionInput) =>
    request<EnquirySummary>(`/consultation-requests/${publicId}/convert`, {
      method: "POST",
      body: JSON.stringify(body),
    }),
};
