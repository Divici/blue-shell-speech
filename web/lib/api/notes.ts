import "server-only";

import { getSession } from "@/lib/auth/session";
import type { GoalValue } from "@/lib/goal-schema";

/**
 * Goals and clinical notes.
 *
 * Same rules as every other client here: `server-only`, provider identity from the
 * encrypted session cookie, never cached.
 */

export interface Goal {
  publicId: string;
  goalText: string;
  domain: string;
  targetCriteria: string | null;
  cueLevelExpected: string | null;
  status: "Active" | "Met" | "Discontinued" | "OnHold";
  startDate: string;
  endDate: string | null;
  aacModality: string | null;
  aacDeviceNotes: string | null;
}

/** What a met/discontinue call answers with. */
export interface GoalTransition {
  publicId: string;
  status: Goal["status"];
}

export interface ClinicalNote {
  publicId: string;
  versionNumber: number;
  isCurrent: boolean;
  status: "Draft" | "Signed" | "Amended";
  subjective: string;
  objective: string;
  assessment: string;
  plan: string;
  origin: string;
  signedAtUtc: string | null;
  signedBy: string | null;
  amendmentReason: string | null;
  /** False means the stored hash no longer matches the content — tampering. */
  integrityVerified: boolean;
}

function apiBaseUrl(): string {
  const url = process.env.API_BASE_URL;
  if (!url) throw new Error("API_BASE_URL is not configured.");
  return url.replace(/\/$/, "");
}

export class ApiConflictError extends Error {}

async function request<T>(path: string, init?: RequestInit): Promise<T | null> {
  const session = await getSession();
  if (!session) throw new Error("No provider session.");

  const response = await fetch(`${apiBaseUrl()}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      "X-Provider-Id": session.providerPublicId,
      ...init?.headers,
    },
    cache: "no-store",
  });

  if (response.status === 404) return null;

  /*
   * 409 carries a message written for a clinician — "this note is signed, create an
   * amendment instead". It is surfaced rather than flattened into a generic failure,
   * because it describes a rule the user needs to understand, not a malfunction.
   */
  if (response.status === 409) {
    const body = (await response.json().catch(() => null)) as { message?: string } | null;
    throw new ApiConflictError(body?.message ?? "That is not allowed in the current state.");
  }

  if (!response.ok) throw new Error(`Notes API ${path} failed with ${response.status}`);
  if (response.status === 204) return null;

  return (await response.json()) as T;
}

export const goalsApi = {
  list: (patientPublicId: string, activeOnly = false) =>
    request<Goal[]>(
      `/patients/${patientPublicId}/goals${activeOnly ? "?activeOnly=true" : ""}`,
    ).then((r) => r ?? []),

  /**
   * The start date is sent EXPLICITLY, never left to the API's default.
   *
   * That default is `DateOnly.FromDateTime(utcNow)`, and at 8pm Eastern the UTC date is
   * already tomorrow. A goal written after an evening visit would be dated a day ahead —
   * the same class of bug D057 fixed on the schedule, in a field nobody would think to
   * check.
   */
  create: (patientPublicId: string, body: GoalValue) =>
    request<{ publicId: string }>(`/patients/${patientPublicId}/goals`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /*
   * Typed, so that a 404 is distinguishable from a success.
   *
   * `request` maps 404 to null. Left as `unknown`, a goal belonging to another provider
   * would be indistinguishable from one closed perfectly well, and the UI would report
   * success for a write that never happened.
   *
   * There is deliberately NO delete here. Marking met and discontinuing are transitions:
   * the row keeps its text and gains an end date, because a closed goal is the record of
   * what therapy accomplished.
   */
  markMet: (patientPublicId: string, goalPublicId: string) =>
    request<GoalTransition>(`/patients/${patientPublicId}/goals/${goalPublicId}/met`, {
      method: "POST",
    }),

  discontinue: (patientPublicId: string, goalPublicId: string) =>
    request<GoalTransition>(
      `/patients/${patientPublicId}/goals/${goalPublicId}/discontinue`,
      { method: "POST" },
    ),
};

export const notesApi = {
  forAppointment: (appointmentPublicId: string) =>
    request<ClinicalNote>(`/notes/appointment/${appointmentPublicId}`),

  history: (publicId: string) =>
    request<ClinicalNote[]>(`/notes/${publicId}/history`).then((r) => r ?? []),

  createDraft: (body: {
    appointmentPublicId: string;
    subjective: string;
    objective: string;
    assessment: string;
    plan: string;
  }) => request<ClinicalNote>("/notes", { method: "POST", body: JSON.stringify(body) }),

  updateDraft: (
    publicId: string,
    body: { subjective: string; objective: string; assessment: string; plan: string },
  ) => request<ClinicalNote>(`/notes/${publicId}`, {
    method: "PUT",
    body: JSON.stringify(body),
  }),

  /*
   * The only delete in this client, and it is narrow by construction: the API refuses
   * anything but an unsigned draft with nothing written in it, and so does a database
   * trigger. Nothing here decides that — asking is all this does.
   *
   * TYPED, so a 404 is distinguishable from a success. `request` maps 404 to null, and a
   * 204 would land in the same place — leaving a note that belongs to another provider
   * indistinguishable from one genuinely removed, and the UI reporting a delete that
   * never happened. The API answers with a body for exactly this reason.
   */
  discardDraft: (publicId: string) =>
    request<{ publicId: string }>(`/notes/${publicId}`, { method: "DELETE" }),

  sign: (publicId: string) =>
    request<ClinicalNote>(`/notes/${publicId}/sign`, { method: "POST" }),

  amend: (publicId: string, reason: string) =>
    request<ClinicalNote>(`/notes/${publicId}/amend`, {
      method: "POST",
      body: JSON.stringify({ reason }),
    }),
};

/*
 * The enum labels used to live here and now live in lib/goal-schema.ts.
 *
 * This module is `server-only`, so a client component importing a label from it would
 * fail the build — and the goal form needs the same list the validator uses. One
 * client-safe source keeps the picker from offering a value the API would reject.
 */
