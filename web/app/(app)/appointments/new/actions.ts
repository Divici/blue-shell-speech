"use server";

import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { scheduleApi, SchedulingConflictError, formatVisitTime } from "@/lib/api/schedule";
import { practiceLocalToUtc, utcToPracticeDate } from "@/lib/practice-time";
import type { ScheduleVisitState } from "./state";

/**
 * Books a visit.
 *
 * The date and time arrive as Michelle's wall clock and are converted here, on the server,
 * using the practice timezone. Doing it in the browser would use the DEVICE's zone — and a
 * phone still set to another timezone would silently book an hour out.
 */
export async function scheduleVisit(
  _previous: ScheduleVisitState,
  formData: FormData,
): Promise<ScheduleVisitState> {
  const values = {
    patientPublicId: String(formData.get("patientPublicId") ?? ""),
    appointmentType: String(formData.get("appointmentType") ?? "Therapy"),
    date: String(formData.get("date") ?? ""),
    time: String(formData.get("time") ?? ""),
    durationMinutes: String(formData.get("durationMinutes") ?? "60"),
    travelBlockMinutes: String(formData.get("travelBlockMinutes") ?? "0"),
    notes: String(formData.get("notes") ?? ""),
  };

  const errors: ScheduleVisitState["errors"] = {};

  if (!values.patientPublicId) errors.patientPublicId = "Choose a patient.";
  if (!values.date) errors.date = "Pick a date.";
  if (!values.time) errors.time = "Pick a start time.";

  if (Object.keys(errors).length > 0) {
    return { status: "error", errors, values };
  }

  let startUtc: Date;
  try {
    startUtc = practiceLocalToUtc(values.date, values.time);
  } catch {
    return {
      status: "error",
      errors: { date: "That date and time could not be read." },
      values,
    };
  }

  try {
    const created = await scheduleApi.create({
      patientPublicId: values.patientPublicId,
      appointmentType: values.appointmentType,
      startUtc: startUtc.toISOString(),
      durationMinutes: Number(values.durationMinutes),
      travelBlockMinutes: Number(values.travelBlockMinutes) || null,
      notes: values.notes.trim() || null,
    });

    if (!created) {
      return {
        status: "error",
        errors: {},
        values,
        message: "That patient could not be found.",
      };
    }
  } catch (error) {
    if (error instanceof SchedulingConflictError) {
      /*
       * Names the clashing visit's time.
       *
       * "That overlaps another visit" leaves Michelle to go and find it. Telling her
       * WHICH one — and that travel time is why, when the calendar looks clear — is the
       * difference between a useful refusal and an obstruction.
       */
      const clashAt = error.conflictingStartUtc
        ? formatVisitTime(error.conflictingStartUtc)
        : null;

      return {
        status: "error",
        errors: {},
        values,
        message: clashAt
          ? `${error.message} The other visit starts at ${clashAt}.`
          : error.message,
      };
    }

    return {
      status: "error",
      errors: {},
      values,
      message: "We could not book this visit. Please try again.",
    };
  }

  const day = utcToPracticeDate(startUtc);
  revalidatePath("/today");
  redirect(`/today?date=${day}`);
}
