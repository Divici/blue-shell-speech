import "server-only";

import { getSession } from "@/lib/auth/session";

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
  });

  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`Schedule API ${path} failed with ${response.status}`);
  return (await response.json()) as T;
}

export const scheduleApi = {
  /** `date` is a LOCAL practice date (yyyy-mm-dd), not a UTC instant. */
  day: (date: string) => request<DaySchedule>(`/appointments/day/${date}`),
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
  }).format(new Date(startUtc));
}

export function formatVisitRange(startUtc: string, durationMinutes: number): string {
  const start = new Date(startUtc);
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
