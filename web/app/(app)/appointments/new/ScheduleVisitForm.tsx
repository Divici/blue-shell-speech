"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import Link from "next/link";
import { scheduleVisit } from "./actions";
import { INITIAL_SCHEDULE_STATE, APPOINTMENT_TYPES } from "./state";
import type { PatientSummary } from "@/lib/api/patients";
import { DURATION_OPTIONS, TRAVEL_OPTIONS } from "@/lib/practice-time";

const field = "w-full rounded-xl border bg-white px-4 py-3 text-ink outline-none transition-colors";
const border = (hasError: boolean) =>
  hasError ? "border-coral" : "border-ice focus:border-blue";

function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="rounded-full bg-blue-action px-6 py-3 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending ? "Booking…" : "Book visit"}
    </button>
  );
}

interface ScheduleVisitFormProps {
  patients: PatientSummary[];
  defaultDate: string;
}

export function ScheduleVisitForm({ patients, defaultDate }: ScheduleVisitFormProps) {
  const [state, formAction] = useActionState(scheduleVisit, INITIAL_SCHEDULE_STATE);

  // Uncontrolled inputs remounted on echo — same reasoning as the consultation form:
  // React resets a form after an action, and defaultValue does not survive that.
  const [echoed, setEchoed] = useState(state.values);
  const [generation, setGeneration] = useState(0);

  if (state.values !== echoed) {
    setEchoed(state.values);
    setGeneration((g) => g + 1);
  }

  const values = state.values ?? {
    patientPublicId: "",
    appointmentType: "Therapy",
    date: defaultDate,
    time: "",
    durationMinutes: "60",
    travelBlockMinutes: "0",
    notes: "",
  };

  if (patients.length === 0) {
    return (
      <div className="mt-6 rounded-2xl border border-ice bg-white p-8 text-center">
        <p className="font-semibold text-navy">No patients yet.</p>
        <p className="mt-1 text-sm text-ink-muted">
          A visit needs someone to visit. Add a patient first.
        </p>
        <Link
          href="/patients/new"
          className="mt-5 inline-block rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white"
        >
          Add patient
        </Link>
      </div>
    );
  }

  return (
    <form
      key={generation}
      action={formAction}
      noValidate
      className="mt-6 rounded-2xl border border-ice bg-white p-6 sm:p-8"
    >
      {state.message && (
        <div
          role="alert"
          className="mb-6 rounded-xl border border-sand bg-sand/25 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      <div className="grid gap-5 sm:grid-cols-2">
        <div className="sm:col-span-2">
          <label htmlFor="patientPublicId" className="mb-1.5 block text-sm font-semibold text-navy">
            Patient
          </label>
          <select
            id="patientPublicId"
            name="patientPublicId"
            defaultValue={values.patientPublicId}
            aria-invalid={Boolean(state.errors.patientPublicId)}
            className={`${field} ${border(Boolean(state.errors.patientPublicId))}`}
          >
            <option value="">Choose a patient…</option>
            {patients.map((patient) => (
              <option key={patient.publicId} value={patient.publicId}>
                {patient.lastName}, {patient.firstName}
              </option>
            ))}
          </select>
          {state.errors.patientPublicId && (
            <p className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.patientPublicId}
            </p>
          )}
        </div>

        <div>
          <label htmlFor="appointmentType" className="mb-1.5 block text-sm font-semibold text-navy">
            Type
          </label>
          <select
            id="appointmentType"
            name="appointmentType"
            defaultValue={values.appointmentType}
            className={`${field} border-ice focus:border-blue`}
          >
            {APPOINTMENT_TYPES.map((type) => (
              <option key={type.value} value={type.value}>
                {type.label}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label htmlFor="durationMinutes" className="mb-1.5 block text-sm font-semibold text-navy">
            Duration
          </label>
          <select
            id="durationMinutes"
            name="durationMinutes"
            defaultValue={values.durationMinutes}
            className={`${field} border-ice focus:border-blue`}
          >
            {DURATION_OPTIONS.map((minutes) => (
              <option key={minutes} value={minutes}>
                {minutes} minutes
              </option>
            ))}
          </select>
        </div>

        <div>
          <label htmlFor="date" className="mb-1.5 block text-sm font-semibold text-navy">
            Date
          </label>
          <input
            id="date"
            name="date"
            type="date"
            defaultValue={values.date}
            aria-invalid={Boolean(state.errors.date)}
            className={`${field} ${border(Boolean(state.errors.date))}`}
          />
          {state.errors.date && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.date}</p>
          )}
        </div>

        <div>
          <label htmlFor="time" className="mb-1.5 block text-sm font-semibold text-navy">
            Start time
          </label>
          <input
            id="time"
            name="time"
            type="time"
            defaultValue={values.time}
            aria-invalid={Boolean(state.errors.time)}
            aria-describedby="time-hint"
            className={`${field} ${border(Boolean(state.errors.time))}`}
          />
          <p id="time-hint" className="mt-1.5 text-sm text-ink-muted">
            Eastern time, the same clock you work in.
          </p>
          {state.errors.time && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.time}</p>
          )}
        </div>

        <div className="sm:col-span-2">
          <label
            htmlFor="travelBlockMinutes"
            className="mb-1.5 block text-sm font-semibold text-navy"
          >
            Travel time before this visit
          </label>
          <select
            id="travelBlockMinutes"
            name="travelBlockMinutes"
            defaultValue={values.travelBlockMinutes}
            aria-describedby="travel-hint"
            className={`${field} border-ice focus:border-blue sm:max-w-xs`}
          >
            {TRAVEL_OPTIONS.map((minutes) => (
              <option key={minutes} value={minutes}>
                {minutes === 0 ? "None" : `${minutes} minutes`}
              </option>
            ))}
          </select>
          <p id="travel-hint" className="mt-1.5 text-sm text-ink-muted">
            Counted when checking for clashes — two visits can be an hour apart on the
            calendar and still impossible to make.
          </p>
        </div>
      </div>

      <div className="mt-5">
        <label htmlFor="notes" className="mb-1.5 block text-sm font-semibold text-navy">
          Notes <span className="font-normal text-ink-muted">(optional)</span>
        </label>
        <textarea
          id="notes"
          name="notes"
          rows={3}
          maxLength={1000}
          defaultValue={values.notes}
          className={`${field} border-ice focus:border-blue resize-y`}
        />
      </div>

      <div className="mt-7 flex items-center gap-4">
        <SubmitButton />
        <Link href="/today" className="text-sm font-medium text-ink-muted hover:text-blue-deep">
          Cancel
        </Link>
      </div>
    </form>
  );
}
