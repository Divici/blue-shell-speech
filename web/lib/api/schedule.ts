import "server-only";

import { getSession } from "@/lib/auth/session";
import { apiSignal } from "@/lib/api/timeouts";
import { parseApiInstant } from "@/lib/practice-time";

/**
 * Scheduling API client.
 *
 * Same rules as the patient client: `server-only`, provider identity read from the
 * encrypted session cookie, never cached.
 */

export interface DayVisit {
  publicId: string;
  patientPublicId: string;
  patientFirstName: string;
  patientLastName: string;
  appointmentType: string;
  startUtc: string;
  durationMinutes: number;
  status: "Scheduled" | "Completed" | "Cancelled" | "NoShow";
  travelBlockMinutes: number | null;
  mileage: number | null;
  notes: string | null;
  /*
   * The CURRENT clinical note for this visit, or null if it has not been documented yet.
   *
   * Carried on the day payload deliberately: the schedule needs to offer "open the note"
   * or "start one" per visit, and asking per card would be a request per visit through
   * the BFF to a container that scales to zero. One call answers the whole day.
   */
  notePublicId: string | null;
  noteStatus: string | null;
}

export interface DaySchedule {
  date: string;
  visits: DayVisit[];
  totalMileage: number;
}

function apiBaseUrl(): string {
  const url = process.env.API_BASE_URL;
  if (!url) throw new Error("API_BASE_URL is not configured.");
  return url.replace(/\/$/, "");
}

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
    // The API bounds its own requests and answers 504 past that; this bounds the case
    // where no answer arrives at all. lib/api/timeouts.ts carries the arithmetic.
    signal: apiSignal(),
  });

  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`Schedule API ${path} failed with ${response.status}`);
  return (await response.json()) as T;
}

/** A visit that could not be booked because it overlaps another, travel time included. */
export class SchedulingConflictError extends Error {
  constructor(
    message: string,
    readonly conflictingStartUtc: string | null,
  ) {
    super(message);
  }
}

export const scheduleApi = {
  /** `date` is a LOCAL practice date (yyyy-mm-dd), not a UTC instant. */
  day: (date: string) => request<DaySchedule>(`/appointments/day/${date}`),

  /**
   * Books a visit.
   *
   * `startUtc` must already be a UTC instant — the API rejects anything else, and the
   * conversion from Michelle's wall clock happens in lib/practice-time.ts where it is
   * tested against both DST boundaries.
   */
  create: async (body: {
    patientPublicId: string;
    appointmentType: string;
    startUtc: string;
    durationMinutes: number;
    travelBlockMinutes: number | null;
    notes: string | null;
  }): Promise<{ publicId: string } | null> => {
    const session = await getSession();
    if (!session) throw new Error("No provider session.");

    const response = await fetch(`${apiBaseUrl()}/appointments`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Provider-Id": session.providerPublicId,
      },
      body: JSON.stringify(body),
      cache: "no-store",
      // A second fetch in this file, and therefore a second place to forget the bound.
      // See lib/api/timeouts.ts.
      signal: apiSignal(),
    });

    if (response.status === 404) return null;

    /*
     * 409 is not a failure to report generically.
     *
     * It means the visit clashes with another once travel time is counted (D056) — a
     * fact Michelle needs to act on, and one a plain "could not save" would hide. The
     * conflicting start time comes back so the UI can name it.
     */
    if (response.status === 409) {
      const body = (await response.json().catch(() => null)) as {
        message?: string;
        conflictingStartUtc?: string;
      } | null;

      throw new SchedulingConflictError(
        body?.message ?? "That overlaps another visit.",
        body?.conflictingStartUtc ?? null,
      );
    }

    if (!response.ok) {
      throw new Error(`Scheduling failed with ${response.status}`);
    }

    return (await response.json()) as { publicId: string };
  },
};

/**
 * The practice's timezone.
 *
 * Every stored time is UTC; every displayed time is America/New_York (CLAUDE.md
 * conventions). Formatting happens on the SERVER so the rendered page does not depend on
 * the viewer's device clock — a clinician checking the schedule on a phone still set to
 * another zone must see the practice's day, not theirs.
 */
export const PRACTICE_TIME_ZONE = "America/New_York";

export function formatVisitTime(startUtc: string): string {
  return new Intl.DateTimeFormat("en-US", {
    hour: "numeric",
    minute: "2-digit",
    timeZone: PRACTICE_TIME_ZONE,
  }).format(parseApiInstant(startUtc));
}

export function formatVisitRange(startUtc: string, durationMinutes: number): string {
  const start = parseApiInstant(startUtc);
  const end = new Date(start.getTime() + durationMinutes * 60_000);

  const formatter = new Intl.DateTimeFormat("en-US", {
    hour: "numeric",
    minute: "2-digit",
    timeZone: PRACTICE_TIME_ZONE,
  });

  return `${formatter.format(start)} – ${formatter.format(end)}`;
}

/** Today's date in the PRACTICE's timezone, as yyyy-mm-dd. */
export function practiceToday(now = new Date()): string {
  // en-CA yields ISO-ordered parts, which is the shortest correct way to get yyyy-mm-dd
  // for a specific zone without hand-rolling the arithmetic.
  return new Intl.DateTimeFormat("en-CA", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    timeZone: PRACTICE_TIME_ZONE,
  }).format(now);
}

export function shiftDate(isoDate: string, days: number): string {
  const date = new Date(`${isoDate}T12:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}
