import "server-only";

import { getSession } from "@/lib/auth/session";

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

  create: (
    patientPublicId: string,
    body: {
      goalText: string;
      domain: string;
      targetCriteria: string | null;
      cueLevelExpected: string | null;
      aacModality: string | null;
      aacDeviceNotes: string | null;
    },
  ) => request<{ publicId: string }>(`/patients/${patientPublicId}/goals`, {
    method: "POST",
    body: JSON.stringify({ ...body, startDate: null }),
  }),

  markMet: (patientPublicId: string, goalPublicId: string) =>
    request(`/patients/${patientPublicId}/goals/${goalPublicId}/met`, { method: "POST" }),

  discontinue: (patientPublicId: string, goalPublicId: string) =>
    request(`/patients/${patientPublicId}/goals/${goalPublicId}/discontinue`, {
      method: "POST",
    }),
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

  sign: (publicId: string) =>
    request<ClinicalNote>(`/notes/${publicId}/sign`, { method: "POST" }),

  amend: (publicId: string, reason: string) =>
    request<ClinicalNote>(`/notes/${publicId}/amend`, {
      method: "POST",
      body: JSON.stringify({ reason }),
    }),
};

/** Human labels for the domain enum. "Aac" reads badly; "AAC" is the term. */
export const GOAL_DOMAIN_LABELS: Record<string, string> = {
  Articulation: "Articulation",
  ReceptiveLanguage: "Receptive language",
  ExpressiveLanguage: "Expressive language",
  SocialCommunication: "Social communication",
  Fluency: "Fluency",
  Feeding: "Feeding",
  Aac: "AAC",
};

export const CUE_LEVEL_LABELS: Record<string, string> = {
  Independent: "Independent",
  Visual: "Visual cues",
  Gestural: "Gestural cues",
  Verbal: "Verbal cues",
  Tactile: "Tactile cues",
  HandOverHand: "Hand over hand",
};
