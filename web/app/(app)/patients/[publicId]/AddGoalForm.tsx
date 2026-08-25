"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { addGoal } from "./actions";
import { EMPTY_GOAL_VALUES, INITIAL_ADD_GOAL_STATE, type AddGoalState } from "./state";
import {
  isAacDomain,
  AAC_MODALITIES,
  CUE_LEVELS,
  GOAL_DOMAINS,
} from "@/lib/goal-schema";

const field = "w-full rounded-xl border bg-white px-4 py-3 text-ink outline-none transition-colors";
const border = (hasError: boolean) =>
  hasError ? "border-coral" : "border-ice focus:border-blue";
const labelClass = "mb-1.5 block text-sm font-semibold text-navy";

function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="rounded-full bg-blue-action px-6 py-3 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending ? "Adding…" : "Add goal"}
    </button>
  );
}

interface AddGoalFormProps {
  patientPublicId: string;
  /** The practice-local date, resolved on the server so a device clock cannot shift it. */
  defaultStartDate: string;
  /** Injected only by tests, to render a state the action would have produced. */
  initialState?: AddGoalState;
}

export function AddGoalForm({
  patientPublicId,
  defaultStartDate,
  initialState = INITIAL_ADD_GOAL_STATE,
}: AddGoalFormProps) {
  const [state, formAction] = useActionState(addGoal, initialState);

  /*
   * Uncontrolled inputs remounted on echo — the same treatment the consultation and
   * scheduling forms use. React 19 resets a form after an action, and defaultValue does
   * not survive that, so a rejected goal would come back with every field blank.
   */
  const [echoed, setEchoed] = useState(state.values);
  const [generation, setGeneration] = useState(0);

  /*
   * The selected domain decides whether the AAC fields exist. It is the ONE piece of
   * client state here, and it has to be reset in lockstep with the remount: after a
   * successful add the form comes back empty with the default domain, and a stale "Aac"
   * here would leave AAC fields on screen above a select reading "Articulation".
   */
  const [domain, setDomain] = useState(
    initialState.values?.domain || EMPTY_GOAL_VALUES.domain,
  );

  if (state.values !== echoed) {
    setEchoed(state.values);
    setGeneration((g) => g + 1);
    setDomain(state.values?.domain || EMPTY_GOAL_VALUES.domain);
  }

  const values = state.values ?? { ...EMPTY_GOAL_VALUES, startDate: defaultStartDate };
  const showAac = isAacDomain(domain);

  return (
    <form
      key={generation}
      action={formAction}
      noValidate
      className="mt-6 rounded-2xl border border-ice bg-white p-6"
    >
      <input type="hidden" name="patientPublicId" value={patientPublicId} />

      {state.message && (
        <div
          role="alert"
          className="mb-6 rounded-xl border border-sand bg-sand/25 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      <div>
        <label htmlFor="goalText" className={labelClass}>
          Goal
        </label>
        <textarea
          id="goalText"
          name="goalText"
          rows={3}
          maxLength={1000}
          defaultValue={values.goalText}
          aria-invalid={Boolean(state.errors.goalText)}
          aria-describedby="goalText-hint"
          className={`${field} ${border(Boolean(state.errors.goalText))} resize-y`}
        />
        <p id="goalText-hint" className="mt-1.5 text-sm text-ink-muted">
          In your own words — this is what the note is written against.
        </p>
        {state.errors.goalText && (
          <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.goalText}</p>
        )}
      </div>

      <div className="mt-5 grid gap-5 sm:grid-cols-2">
        <div>
          <label htmlFor="domain" className={labelClass}>
            Domain
          </label>
          <select
            id="domain"
            name="domain"
            defaultValue={values.domain}
            onChange={(event) => setDomain(event.target.value)}
            aria-invalid={Boolean(state.errors.domain)}
            className={`${field} ${border(Boolean(state.errors.domain))}`}
          >
            {GOAL_DOMAINS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
          {state.errors.domain && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.domain}</p>
          )}
        </div>

        <div>
          <label htmlFor="startDate" className={labelClass}>
            Start date
          </label>
          <input
            id="startDate"
            name="startDate"
            type="date"
            defaultValue={values.startDate || defaultStartDate}
            aria-invalid={Boolean(state.errors.startDate)}
            className={`${field} ${border(Boolean(state.errors.startDate))}`}
          />
          {state.errors.startDate && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.startDate}</p>
          )}
        </div>

        <div className="sm:col-span-2">
          <label htmlFor="targetCriteria" className={labelClass}>
            Target criteria <span className="font-normal text-ink-muted">(optional)</span>
          </label>
          <input
            id="targetCriteria"
            name="targetCriteria"
            type="text"
            maxLength={500}
            defaultValue={values.targetCriteria}
            aria-invalid={Boolean(state.errors.targetCriteria)}
            aria-describedby="targetCriteria-hint"
            className={`${field} ${border(Boolean(state.errors.targetCriteria))}`}
          />
          {/* Free text on purpose (presearch §5.4) — anything that parsed this into a rule
              engine would be wrong for the next goal. The hint says so plainly. */}
          <p id="targetCriteria-hint" className="mt-1.5 text-sm text-ink-muted">
            However you measure it — &ldquo;80% accuracy over 3 consecutive sessions&rdquo;.
          </p>
          {state.errors.targetCriteria && (
            <p className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.targetCriteria}
            </p>
          )}
        </div>

        <div className="sm:col-span-2">
          <label htmlFor="cueLevelExpected" className={labelClass}>
            Expected cue level{" "}
            <span className="font-normal text-ink-muted">(optional)</span>
          </label>
          <select
            id="cueLevelExpected"
            name="cueLevelExpected"
            defaultValue={values.cueLevelExpected}
            aria-invalid={Boolean(state.errors.cueLevelExpected)}
            aria-describedby="cueLevelExpected-hint"
            className={`${field} ${border(Boolean(state.errors.cueLevelExpected))} sm:max-w-xs`}
          >
            <option value="">Not specified</option>
            {CUE_LEVELS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
          {/* Ordered most to least independent, because "improving" means moving up it. */}
          <p id="cueLevelExpected-hint" className="mt-1.5 text-sm text-ink-muted">
            Listed most independent first.
          </p>
          {state.errors.cueLevelExpected && (
            <p className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.cueLevelExpected}
            </p>
          )}
        </div>
      </div>

      {/*
        AAC fields, RENDERED ONLY ON AN AAC GOAL — and unmounted, never hidden.

        A hidden input still posts its value, so hiding would send AAC details on an
        articulation goal: rejected by the aggregate, rejected again by
        CK_Goals_AacFieldsOnlyOnAacGoals, and shown to the clinician as an error about
        fields she cannot see. Removing them is the only version where the form, the
        aggregate and the database are asking the same question.

        Hiding-but-focusable would also be an accessibility defect in its own right.
      */}
      {showAac && (
        <fieldset className="mt-5 rounded-xl border border-ice bg-mist p-5">
          <legend className="px-2 text-sm font-semibold text-navy">AAC details</legend>

          <div className="grid gap-5">
            <div>
              <label htmlFor="aacModality" className={labelClass}>
                AAC modality <span className="font-normal text-ink-muted">(optional)</span>
              </label>
              <select
                id="aacModality"
                name="aacModality"
                defaultValue={values.aacModality}
                aria-invalid={Boolean(state.errors.aacModality)}
                className={`${field} ${border(Boolean(state.errors.aacModality))}`}
              >
                <option value="">Not chosen yet</option>
                {AAC_MODALITIES.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
              {state.errors.aacModality && (
                <p className="mt-1.5 text-sm font-medium text-navy">
                  {state.errors.aacModality}
                </p>
              )}
            </div>

            <div>
              <label htmlFor="aacDeviceNotes" className={labelClass}>
                Device or system notes{" "}
                <span className="font-normal text-ink-muted">(optional)</span>
              </label>
              <textarea
                id="aacDeviceNotes"
                name="aacDeviceNotes"
                rows={2}
                maxLength={500}
                defaultValue={values.aacDeviceNotes}
                aria-invalid={Boolean(state.errors.aacDeviceNotes)}
                className={`${field} ${border(Boolean(state.errors.aacDeviceNotes))} resize-y`}
              />
              {state.errors.aacDeviceNotes && (
                <p className="mt-1.5 text-sm font-medium text-navy">
                  {state.errors.aacDeviceNotes}
                </p>
              )}
            </div>
          </div>
        </fieldset>
      )}

      <div className="mt-7">
        <SubmitButton />
      </div>
    </form>
  );
}
