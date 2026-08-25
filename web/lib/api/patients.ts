import "server-only";

import { getSession } from "@/lib/auth/session";
import type { GuardianValue } from "@/lib/guardian-schema";
import type { AddressCorrectionValue, NewAddressValue } from "@/lib/address-schema";

/**
 * Patient API client.
 *
 * `server-only`: importing this from a Client Component is a build error, not a runtime
 * leak of the internal API URL or of PHI into a browser bundle.
 *
 * Every call forwards the provider identity read from the ENCRYPTED SESSION COOKIE. The
 * caller never supplies it, and no page passes one in — a provider id from a request body
 * would be a visitor choosing whose records to read.
 */

export interface PatientSummary {
  publicId: string;
  firstName: string;
  lastName: string;
  dateOfBirth: string;
  status: "Active" | "Inactive" | "Discharged";
}

export interface Guardian {
  publicId: string;
  firstName: string;
  lastName: string;
  relationship: string;
  phone: string | null;
  email: string | null;
  isPrimaryContact: boolean;
  hasLegalAuthority: boolean;
}

export interface PatientAddress {
  publicId: string;
  line1: string;
  line2: string | null;
  city: string;
  state: string;
  postalCode: string;
  addressType: "Session" | "Billing";
  notes: string | null;
  isCurrent: boolean;
  /**
   * The record is VERSIONED: a move closes the previous row rather than overwriting it.
   * These are what let the page say which address a visit last spring happened at —
   * `isCurrent` alone cannot. Both are DateOnly (yyyy-mm-dd), never instants.
   */
  effectiveFrom: string;
  effectiveTo: string | null;
}

export interface PatientDetail extends PatientSummary {
  clinicalSummary: string | null;
  guardians: Guardian[];
  addresses: PatientAddress[];
}

export class NotAuthenticatedError extends Error {}

function apiBaseUrl(): string {
  const url = process.env.API_BASE_URL;
  if (!url) {
    throw new Error("API_BASE_URL is not configured.");
  }
  return url.replace(/\/$/, "");
}

async function request<T>(
  path: string,
  init?: RequestInit,
): Promise<T | null> {
  const session = await getSession();
  if (!session) throw new NotAuthenticatedError("No provider session.");

  const response = await fetch(`${apiBaseUrl()}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      // Read from the session cookie, never from the request.
      "X-Provider-Id": session.providerPublicId,
      ...init?.headers,
    },
    // PHI must never be cached, at any layer.
    cache: "no-store",
  });

  /*
   * 404 becomes null, not an exception.
   *
   * The API returns 404 for a record belonging to another provider as well as for one
   * that does not exist — deliberately, so the two are indistinguishable. Treating them
   * identically here preserves that: the UI cannot render a different message for
   * "forbidden" because it is never told.
   */
  if (response.status === 404) return null;

  if (!response.ok) {
    throw new Error(`Patient API ${path} failed with ${response.status}`);
  }

  if (response.status === 204) return null;
  return (await response.json()) as T;
}

export const patientsApi = {
  list: (search?: string, includeDischarged = false) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    if (includeDischarged) params.set("includeDischarged", "true");
    const query = params.toString();

    return request<PatientSummary[]>(`/patients${query ? `?${query}` : ""}`).then(
      (result) => result ?? [],
    );
  },

  get: (publicId: string) => request<PatientDetail>(`/patients/${publicId}`),

  create: (body: {
    firstName: string;
    lastName: string;
    dateOfBirth: string;
    clinicalSummary: string | null;
  }) =>
    request<PatientDetail>("/patients", {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /*
   * GUARDIANS.
   *
   * `Required<GuardianValue>` on purpose: the schema leaves hasLegalAuthority UNDEFINED
   * when nobody answered, and this signature refuses to compile with it missing. The API
   * refuses a null of its own, so "nobody said" is stopped by the form, by the type, and
   * by the endpoint — none of the three ever turns silence into a recorded decision about
   * who may hold a child's medical file.
   */
  addGuardian: (publicId: string, body: Required<GuardianValue>) =>
    request<PatientDetail>(`/patients/${publicId}/guardians`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  updateGuardian: (
    publicId: string,
    guardianPublicId: string,
    body: Required<GuardianValue>,
  ) =>
    request<PatientDetail>(`/patients/${publicId}/guardians/${guardianPublicId}`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),

  /*
   * ADDRESSES — two calls, because they are two events.
   *
   * `addAddress` records a MOVE: the API closes the current address of that type and keeps
   * it, because a note describing a visit last spring refers to where the family lived
   * then. `correctAddress` fixes a TYPO in place, and its body cannot carry a type or a
   * date — that absence is the guard, not a convention.
   */
  addAddress: (publicId: string, body: NewAddressValue) =>
    request<PatientDetail>(`/patients/${publicId}/addresses`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  correctAddress: (
    publicId: string,
    addressPublicId: string,
    body: AddressCorrectionValue,
  ) =>
    request<PatientDetail>(`/patients/${publicId}/addresses/${addressPublicId}`, {
      method: "PUT",
      body: JSON.stringify(body),
    }),

  discharge: (publicId: string) =>
    request<PatientDetail>(`/patients/${publicId}/discharge`, { method: "POST" }),
};

/** Age in whole months — the unit early-intervention eligibility uses. */
export function ageInMonths(dateOfBirth: string, asOf = new Date()): number {
  const dob = new Date(`${dateOfBirth}T00:00:00Z`);
  let months =
    (asOf.getUTCFullYear() - dob.getUTCFullYear()) * 12 +
    asOf.getUTCMonth() -
    dob.getUTCMonth();
  if (asOf.getUTCDate() < dob.getUTCDate()) months--;
  return Math.max(0, months);
}

/** "2y 6m", or "7m" under a year — how clinicians actually say it. */
export function formatAge(dateOfBirth: string, asOf = new Date()): string {
  const months = ageInMonths(dateOfBirth, asOf);
  const years = Math.floor(months / 12);
  const remainder = months % 12;
  return years === 0 ? `${remainder}m` : `${years}y ${remainder}m`;
}
