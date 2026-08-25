/**
 * Kept out of actions.ts: a "use server" module may only export async functions.
 */
export interface ScheduleVisitValues {
  patientPublicId: string;
  appointmentType: string;
  date: string;
  time: string;
  durationMinutes: string;
  travelBlockMinutes: string;
  notes: string;
}

export interface ScheduleVisitState {
  status: "idle" | "error";
  errors: Partial<Record<keyof ScheduleVisitValues, string>>;
  values?: ScheduleVisitValues;
  /** A whole-form problem — most often a scheduling conflict. */
  message?: string;
}

export const INITIAL_SCHEDULE_STATE: ScheduleVisitState = {
  status: "idle",
  errors: {},
};

export const APPOINTMENT_TYPES = [
  { value: "Therapy", label: "Therapy" },
  { value: "Evaluation", label: "Evaluation" },
  { value: "Consultation", label: "Consultation" },
  { value: "Reassessment", label: "Reassessment" },
] as const;
