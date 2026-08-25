"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { Spinner } from "@/components/loading/Spinner";
import Link from "next/link";
import { createPatient } from "./actions";
import { INITIAL_NEW_PATIENT_STATE, EMPTY_NEW_PATIENT_VALUES } from "./state";

function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex items-center gap-2.5 rounded-full bg-blue-action px-6 py-3 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending && <Spinner size={16} />}
      {pending ? "Saving…" : "Save patient"}
    </button>
  );
}

const fieldClass =
  "w-full rounded-xl border bg-white px-4 py-3 text-ink outline-none transition-colors";

const border = (hasError: boolean) =>
  hasError ? "border-coral" : "border-ice focus:border-blue";

export function NewPatientForm() {
  const [state, formAction] = useActionState(createPatient, INITIAL_NEW_PATIENT_STATE);

  /*
   * Uncontrolled inputs, remounted when the server echoes values back.
   *
   * Same reasoning as the consultation form: React resets an uncontrolled form after an
   * action completes and ignores defaultValue changes on a mounted input, while fully
   * controlled inputs proved fragile across engines. Remounting via `key` keeps the form
   * serialising exactly what the DOM holds.
   */
  const [echoed, setEchoed] = useState(state.values);
  const [generation, setGeneration] = useState(0);

  if (state.values !== echoed) {
    setEchoed(state.values);
    setGeneration((g) => g + 1);
  }

  const values = state.values ?? EMPTY_NEW_PATIENT_VALUES;

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
          className="mb-6 rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      <div className="grid gap-5 sm:grid-cols-2">
        <div>
          <label htmlFor="firstName" className="mb-1.5 block text-sm font-semibold text-navy">
            First name
          </label>
          <input
            id="firstName"
            name="firstName"
            defaultValue={values.firstName}
            aria-invalid={Boolean(state.errors.firstName)}
            aria-describedby={state.errors.firstName ? "firstName-error" : undefined}
            className={`${fieldClass} ${border(Boolean(state.errors.firstName))}`}
          />
          {state.errors.firstName && (
            <p id="firstName-error" className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.firstName}
            </p>
          )}
        </div>

        <div>
          <label htmlFor="lastName" className="mb-1.5 block text-sm font-semibold text-navy">
            Last name
          </label>
          <input
            id="lastName"
            name="lastName"
            defaultValue={values.lastName}
            aria-invalid={Boolean(state.errors.lastName)}
            aria-describedby={state.errors.lastName ? "lastName-error" : undefined}
            className={`${fieldClass} ${border(Boolean(state.errors.lastName))}`}
          />
          {state.errors.lastName && (
            <p id="lastName-error" className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.lastName}
            </p>
          )}
        </div>

        <div>
          <label htmlFor="dateOfBirth" className="mb-1.5 block text-sm font-semibold text-navy">
            Date of birth
          </label>
          <input
            id="dateOfBirth"
            name="dateOfBirth"
            type="date"
            defaultValue={values.dateOfBirth}
            aria-invalid={Boolean(state.errors.dateOfBirth)}
            aria-describedby={state.errors.dateOfBirth ? "dateOfBirth-error" : "dob-hint"}
            className={`${fieldClass} ${border(Boolean(state.errors.dateOfBirth))}`}
          />
          {state.errors.dateOfBirth ? (
            <p id="dateOfBirth-error" className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.dateOfBirth}
            </p>
          ) : (
            <p id="dob-hint" className="mt-1.5 text-sm text-ink-muted">
              Age in months is calculated from this.
            </p>
          )}
        </div>
      </div>

      <div className="mt-5">
        <label htmlFor="clinicalSummary" className="mb-1.5 block text-sm font-semibold text-navy">
          Clinical summary <span className="font-normal text-ink-muted">(optional)</span>
        </label>
        <textarea
          id="clinicalSummary"
          name="clinicalSummary"
          rows={4}
          maxLength={4000}
          defaultValue={values.clinicalSummary}
          aria-invalid={Boolean(state.errors.clinicalSummary)}
          aria-describedby={state.errors.clinicalSummary ? "summary-error" : "summary-hint"}
          className={`${fieldClass} ${border(Boolean(state.errors.clinicalSummary))} resize-y`}
        />
        {state.errors.clinicalSummary ? (
          <p id="summary-error" className="mt-1.5 text-sm font-medium text-navy">
            {state.errors.clinicalSummary}
          </p>
        ) : (
          <p id="summary-hint" className="mt-1.5 text-sm text-ink-muted">
            Diagnosis context, precautions, anything worth knowing before the first session.
          </p>
        )}
      </div>

      <div className="mt-7 flex items-center gap-4">
        <SubmitButton />
        <Link href="/patients" className="text-sm font-medium text-ink-muted hover:text-blue-deep">
          Cancel
        </Link>
      </div>
    </form>
  );
}
