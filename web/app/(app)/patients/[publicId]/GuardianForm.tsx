"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { Spinner } from "@/components/loading/Spinner";
import { addGuardian, updateGuardian } from "./actions";
import { EMPTY_GUARDIAN_VALUES, INITIAL_GUARDIAN_STATE, type GuardianFormState } from "./state";
import type { GuardianInput } from "@/lib/guardian-schema";

const field = "w-full rounded-xl border bg-white px-4 py-3 text-ink outline-none transition-colors";
const border = (hasError: boolean) =>
  hasError ? "border-coral" : "border-ice focus:border-blue";
const labelClass = "mb-1.5 block text-sm font-semibold text-navy";

function SubmitButton({ editing }: { editing: boolean }) {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex items-center gap-2.5 rounded-full bg-blue-action px-6 py-3 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending && <Spinner size={16} />}
      {pending
        ? editing
          ? "Saving…"
          : "Adding…"
        : editing
          ? "Save changes"
          : "Add guardian"}
    </button>
  );
}

export interface GuardianFormProps {
  patientPublicId: string;
  /**
   * Present when editing an existing guardian, absent when adding one. It decides which
   * action the form posts to and whether an identifier travels with it — nothing else.
   */
  guardianPublicId?: string;
  /** The guardian's current details, when editing. */
  defaults?: GuardianInput;
  /**
   * Namespaces every id on this form.
   *
   * A patient page renders one of these per guardian plus one for adding, so a fixed
   * `id="firstName"` would repeat and every label after the first would point at the wrong
   * input — a real defect for anyone using a screen reader or tapping a label on a phone.
   */
  idPrefix: string;
  /** Injected only by tests, to render a state the action would have produced. */
  initialState?: GuardianFormState;
}

/**
 * Adding and editing a guardian.
 *
 * One component for both, because it is the same form asking the same questions of the
 * same aggregate; only the action and the presence of an id differ.
 *
 * THE LEGAL-AUTHORITY CONTROL IS THE POINT OF THIS FORM. It is a required radio group with
 * NOTHING PRESELECTED, sitting in its own fieldset, well away from the primary-contact
 * checkbox. That shape is deliberate:
 *
 * - A checkbox would submit `false` for "she may not" and for "nobody looked at this", and
 *   those are not the same claim about a family. A radio group with no default cannot be
 *   submitted unanswered, so a stored "no" always means somebody chose it.
 * - It is not derived from, adjacent to, or grouped with the primary contact. A stepparent
 *   can be the adult who brings the child every week and hold no authority to consent; a
 *   non-custodial parent can hold the authority and never appear at a session. Custody
 *   disputes are not an edge case in paediatrics, and a record released to the wrong adult
 *   is a breach.
 *
 * The same question is asked again by validateGuardian, again by the API
 * (`hasLegalAuthority is null` → 400) and again by the aggregate, which reads it from its
 * own argument. Four layers, one question, no defaults anywhere along it.
 */
export function GuardianForm({
  patientPublicId,
  guardianPublicId,
  defaults,
  idPrefix,
  initialState = INITIAL_GUARDIAN_STATE,
}: GuardianFormProps) {
  const editing = Boolean(guardianPublicId);
  const [state, formAction] = useActionState(
    editing ? updateGuardian : addGuardian,
    initialState,
  );

  /*
   * Uncontrolled inputs remounted on echo — the pattern every form in this app uses.
   * React 19 resets a form after an action and defaultValue does not survive that, so a
   * rejected guardian would come back with every field blank.
   */
  const [echoed, setEchoed] = useState(state.values);
  const [generation, setGeneration] = useState(0);

  if (state.values !== echoed) {
    setEchoed(state.values);
    setGeneration((g) => g + 1);
  }

  const values = state.values ?? defaults ?? EMPTY_GUARDIAN_VALUES;
  const id = (name: string) => `${idPrefix}-${name}`;

  return (
    <form key={generation} action={formAction} noValidate className="mt-4">
      <input type="hidden" name="patientPublicId" value={patientPublicId} />
      {guardianPublicId && (
        <input type="hidden" name="guardianPublicId" value={guardianPublicId} />
      )}

      {state.message && (
        <div
          role="alert"
          className="mb-5 rounded-xl border border-sand bg-sand/25 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      <div className="grid gap-5 sm:grid-cols-2">
        <div>
          <label htmlFor={id("firstName")} className={labelClass}>
            First name
          </label>
          <input
            id={id("firstName")}
            name="firstName"
            type="text"
            maxLength={100}
            autoComplete="off"
            defaultValue={values.firstName}
            aria-invalid={Boolean(state.errors.firstName)}
            className={`${field} ${border(Boolean(state.errors.firstName))}`}
          />
          {state.errors.firstName && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.firstName}</p>
          )}
        </div>

        <div>
          <label htmlFor={id("lastName")} className={labelClass}>
            Last name
          </label>
          <input
            id={id("lastName")}
            name="lastName"
            type="text"
            maxLength={100}
            autoComplete="off"
            defaultValue={values.lastName}
            aria-invalid={Boolean(state.errors.lastName)}
            className={`${field} ${border(Boolean(state.errors.lastName))}`}
          />
          {state.errors.lastName && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.lastName}</p>
          )}
        </div>

        <div className="sm:col-span-2">
          <label htmlFor={id("relationship")} className={labelClass}>
            Relationship to the child
          </label>
          {/*
            Free text, because the column is. A picker here would be the UI inventing a
            taxonomy the database does not have — and the list would be wrong for the first
            family it did not fit.
          */}
          <input
            id={id("relationship")}
            name="relationship"
            type="text"
            maxLength={50}
            autoComplete="off"
            defaultValue={values.relationship}
            aria-invalid={Boolean(state.errors.relationship)}
            aria-describedby={id("relationship-hint")}
            className={`${field} ${border(Boolean(state.errors.relationship))} sm:max-w-sm`}
          />
          <p id={id("relationship-hint")} className="mt-1.5 text-sm text-ink-muted">
            However the family describes it — Mother, Stepfather, Grandmother, Foster carer.
          </p>
          {state.errors.relationship && (
            <p className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.relationship}
            </p>
          )}
        </div>

        <div>
          <label htmlFor={id("phone")} className={labelClass}>
            Phone <span className="font-normal text-ink-muted">(optional)</span>
          </label>
          <input
            id={id("phone")}
            name="phone"
            type="tel"
            maxLength={50}
            autoComplete="off"
            defaultValue={values.phone}
            aria-invalid={Boolean(state.errors.phone)}
            className={`${field} ${border(Boolean(state.errors.phone))}`}
          />
          {state.errors.phone && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.phone}</p>
          )}
        </div>

        <div>
          <label htmlFor={id("email")} className={labelClass}>
            Email <span className="font-normal text-ink-muted">(optional)</span>
          </label>
          <input
            id={id("email")}
            name="email"
            type="email"
            maxLength={256}
            autoComplete="off"
            defaultValue={values.email}
            aria-invalid={Boolean(state.errors.email)}
            className={`${field} ${border(Boolean(state.errors.email))}`}
          />
          {state.errors.email && (
            <p className="mt-1.5 text-sm font-medium text-navy">{state.errors.email}</p>
          )}
        </div>
      </div>

      {/*
        Two separate questions, rendered as two separate controls, in that order and with
        the sentence between them saying so. Putting them side by side as a pair of
        checkboxes would invite exactly the inference this whole slice exists to prevent.
      */}
      <div className="mt-6 rounded-xl border border-ice bg-mist p-5">
        <label htmlFor={id("isPrimaryContact")} className="flex items-start gap-3">
          <input
            id={id("isPrimaryContact")}
            name="isPrimaryContact"
            type="checkbox"
            value="yes"
            defaultChecked={values.isPrimaryContact}
            className="mt-1 size-5 shrink-0 rounded border-ice text-blue-action"
          />
          <span>
            {/*
              Worded as an instruction — "make this person…" — where the card above states
              the fact, "Primary contact". A control and a badge that read identically are
              indistinguishable to anything querying the page by its text, tests included,
              and the card's badge is the thing a clinician is actually reading.
            */}
            <span className="block font-semibold text-navy">
              Make this person the primary contact
            </span>
            <span className="mt-0.5 block text-sm text-ink-muted">
              Who to call first when a session has to move. One person per child.
            </span>
          </span>
        </label>
      </div>

      <fieldset className="mt-5 rounded-xl border border-ice bg-white p-5">
        <legend className="px-2 text-sm font-semibold text-navy">
          May this person receive the child&rsquo;s records?
        </legend>

        <p id={id("authority-hint")} className="text-sm leading-relaxed text-ink">
          A separate question from who the primary contact is, and not implied by it. The
          adult who brings a child to sessions is not always the adult entitled to the
          record.
        </p>

        {/*
          NEITHER OPTION IS PRESELECTED when adding.

          There is no third value in the database, so this is the last layer at which
          "nobody answered" is distinguishable from "no". A default would resolve that
          silently and permanently — and in the direction that decides whether a parent can
          get their child's file.
        */}
        <div className="mt-4 flex flex-wrap gap-x-8 gap-y-3">
          {[
            { value: "yes", label: "Yes — they may receive records" },
            { value: "no", label: "No" },
          ].map((option) => (
            <label
              key={option.value}
              htmlFor={id(`authority-${option.value}`)}
              className="flex items-center gap-3 text-ink"
            >
              <input
                id={id(`authority-${option.value}`)}
                name="hasLegalAuthority"
                type="radio"
                value={option.value}
                defaultChecked={values.hasLegalAuthority === option.value}
                aria-describedby={id("authority-hint")}
                className="size-5 shrink-0 border-ice text-blue-action"
              />
              {option.label}
            </label>
          ))}
        </div>

        {state.errors.hasLegalAuthority && (
          <p className="mt-3 text-sm font-medium text-navy">
            {state.errors.hasLegalAuthority}
          </p>
        )}
      </fieldset>

      <div className="mt-6">
        <SubmitButton editing={editing} />
      </div>
    </form>
  );
}
