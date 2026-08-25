"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { correctAddress, recordAddress } from "./actions";
import {
  EMPTY_ADDRESS_VALUES,
  INITIAL_ADDRESS_CORRECTION_STATE,
  INITIAL_ADDRESS_STATE,
  type AddressCorrectionState,
  type AddressFormState,
} from "./state";
import {
  ADDRESS_TYPES,
  type AddressCorrectionInput,
  type NewAddressInput,
} from "@/lib/address-schema";

const field = "w-full rounded-xl border bg-white px-4 py-3 text-ink outline-none transition-colors";
const border = (hasError: boolean) =>
  hasError ? "border-coral" : "border-ice focus:border-blue";
const labelClass = "mb-1.5 block text-sm font-semibold text-navy";

function SubmitButton({ label, busy }: { label: string; busy: string }) {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="rounded-full bg-blue-action px-6 py-3 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending ? busy : label}
    </button>
  );
}

/**
 * The street fields, shared by both forms.
 *
 * Shared because a typo and a move describe the same kind of thing; NOT shared any further
 * than that, because what surrounds them differs — see the two components below.
 */
function AddressFields({
  id,
  values,
  errors,
}: {
  id: (name: string) => string;
  values: AddressCorrectionInput;
  errors: Partial<Record<keyof AddressCorrectionInput, string>>;
}) {
  return (
    <div className="grid gap-5 sm:grid-cols-6">
      <div className="sm:col-span-4">
        <label htmlFor={id("line1")} className={labelClass}>
          Street address
        </label>
        <input
          id={id("line1")}
          name="line1"
          type="text"
          maxLength={200}
          autoComplete="off"
          defaultValue={values.line1}
          aria-invalid={Boolean(errors.line1)}
          className={`${field} ${border(Boolean(errors.line1))}`}
        />
        {errors.line1 && (
          <p className="mt-1.5 text-sm font-medium text-navy">{errors.line1}</p>
        )}
      </div>

      <div className="sm:col-span-2">
        <label htmlFor={id("line2")} className={labelClass}>
          Apt / unit <span className="font-normal text-ink-muted">(optional)</span>
        </label>
        <input
          id={id("line2")}
          name="line2"
          type="text"
          maxLength={200}
          autoComplete="off"
          defaultValue={values.line2}
          aria-invalid={Boolean(errors.line2)}
          className={`${field} ${border(Boolean(errors.line2))}`}
        />
        {errors.line2 && (
          <p className="mt-1.5 text-sm font-medium text-navy">{errors.line2}</p>
        )}
      </div>

      <div className="sm:col-span-3">
        <label htmlFor={id("city")} className={labelClass}>
          City or town
        </label>
        <input
          id={id("city")}
          name="city"
          type="text"
          maxLength={100}
          autoComplete="off"
          defaultValue={values.city}
          aria-invalid={Boolean(errors.city)}
          className={`${field} ${border(Boolean(errors.city))}`}
        />
        {errors.city && (
          <p className="mt-1.5 text-sm font-medium text-navy">{errors.city}</p>
        )}
      </div>

      <div className="sm:col-span-1">
        <label htmlFor={id("state")} className={labelClass}>
          State
        </label>
        <input
          id={id("state")}
          name="state"
          type="text"
          maxLength={2}
          autoComplete="off"
          defaultValue={values.state}
          aria-invalid={Boolean(errors.state)}
          className={`${field} ${border(Boolean(errors.state))} uppercase`}
        />
        {errors.state && (
          <p className="mt-1.5 text-sm font-medium text-navy">{errors.state}</p>
        )}
      </div>

      <div className="sm:col-span-2">
        <label htmlFor={id("postalCode")} className={labelClass}>
          ZIP code
        </label>
        <input
          id={id("postalCode")}
          name="postalCode"
          type="text"
          inputMode="numeric"
          maxLength={20}
          autoComplete="off"
          defaultValue={values.postalCode}
          aria-invalid={Boolean(errors.postalCode)}
          className={`${field} ${border(Boolean(errors.postalCode))}`}
        />
        {errors.postalCode && (
          <p className="mt-1.5 text-sm font-medium text-navy">{errors.postalCode}</p>
        )}
      </div>

      <div className="sm:col-span-6">
        <label htmlFor={id("notes")} className={labelClass}>
          Getting in <span className="font-normal text-ink-muted">(optional)</span>
        </label>
        <input
          id={id("notes")}
          name="notes"
          type="text"
          maxLength={500}
          autoComplete="off"
          defaultValue={values.notes}
          aria-invalid={Boolean(errors.notes)}
          aria-describedby={id("notes-hint")}
          className={`${field} ${border(Boolean(errors.notes))}`}
        />
        {/* Practical, never clinical. This field is read standing on a doorstep. */}
        <p id={id("notes-hint")} className="mt-1.5 text-sm text-ink-muted">
          Gate code, where to park, dog in the yard.
        </p>
        {errors.notes && (
          <p className="mt-1.5 text-sm font-medium text-navy">{errors.notes}</p>
        )}
      </div>
    </div>
  );
}

/**
 * Recording a MOVE.
 *
 * Carries an address type and the date the family moved in, because both are what make the
 * previous address historical rather than wrong: the API closes the current address of the
 * same type as of that date and keeps the row. A note describing a visit last spring refers
 * to where they lived then.
 */
export function RecordAddressForm({
  patientPublicId,
  defaultEffectiveFrom,
  idPrefix,
  initialState = INITIAL_ADDRESS_STATE,
}: {
  patientPublicId: string;
  /** The practice-local date, resolved on the server so a device clock cannot shift it. */
  defaultEffectiveFrom: string;
  idPrefix: string;
  initialState?: AddressFormState;
}) {
  const [state, formAction] = useActionState(recordAddress, initialState);

  const [echoed, setEchoed] = useState(state.values);
  const [generation, setGeneration] = useState(0);

  if (state.values !== echoed) {
    setEchoed(state.values);
    setGeneration((g) => g + 1);
  }

  const values: NewAddressInput = state.values ?? {
    ...EMPTY_ADDRESS_VALUES,
    effectiveFrom: defaultEffectiveFrom,
  };
  const id = (name: string) => `${idPrefix}-${name}`;

  return (
    <form key={generation} action={formAction} noValidate className="mt-4">
      <input type="hidden" name="patientPublicId" value={patientPublicId} />

      {state.message && (
        <div
          role="alert"
          className="mb-5 rounded-xl border border-sand bg-sand/25 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      <AddressFields id={id} values={values} errors={state.errors} />

      <div className="mt-5 grid gap-5 sm:grid-cols-2">
        <div>
          <label htmlFor={id("addressType")} className={labelClass}>
            What is this address for?
          </label>
          <select
            id={id("addressType")}
            name="addressType"
            defaultValue={values.addressType}
            aria-invalid={Boolean(state.errors.addressType)}
            className={`${field} ${border(Boolean(state.errors.addressType))}`}
          >
            {ADDRESS_TYPES.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
          {state.errors.addressType && (
            <p className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.addressType}
            </p>
          )}
        </div>

        <div>
          <label htmlFor={id("effectiveFrom")} className={labelClass}>
            In use from
          </label>
          <input
            id={id("effectiveFrom")}
            name="effectiveFrom"
            type="date"
            defaultValue={values.effectiveFrom || defaultEffectiveFrom}
            aria-invalid={Boolean(state.errors.effectiveFrom)}
            className={`${field} ${border(Boolean(state.errors.effectiveFrom))}`}
          />
          {state.errors.effectiveFrom && (
            <p className="mt-1.5 text-sm font-medium text-navy">
              {state.errors.effectiveFrom}
            </p>
          )}
        </div>
      </div>

      <p className="mt-5 text-sm leading-relaxed text-ink-muted">
        This replaces the current address of the same kind. The old one stays on the record,
        closed as of this date, because past visits happened there.
      </p>

      <div className="mt-5">
        <SubmitButton label="Save address" busy="Saving…" />
      </div>
    </form>
  );
}

/**
 * Fixing a TYPO.
 *
 * NO ADDRESS TYPE AND NO DATES ANYWHERE ON THIS FORM, and their absence is deliberate all
 * the way down: the form does not render them, the action does not read them, and
 * CorrectAddressRequest has no room for them. The family never lived at the mistyped
 * address, so there is no history to keep — and a correction that could move the type or
 * the dates would rewrite where a past visit happened.
 */
export function CorrectAddressForm({
  patientPublicId,
  addressPublicId,
  defaults,
  idPrefix,
  initialState = INITIAL_ADDRESS_CORRECTION_STATE,
}: {
  patientPublicId: string;
  addressPublicId: string;
  defaults: AddressCorrectionInput;
  idPrefix: string;
  initialState?: AddressCorrectionState;
}) {
  const [state, formAction] = useActionState(correctAddress, initialState);

  const [echoed, setEchoed] = useState(state.values);
  const [generation, setGeneration] = useState(0);

  if (state.values !== echoed) {
    setEchoed(state.values);
    setGeneration((g) => g + 1);
  }

  const values = state.values ?? defaults;
  const id = (name: string) => `${idPrefix}-${name}`;

  return (
    <form key={generation} action={formAction} noValidate className="mt-4">
      <input type="hidden" name="patientPublicId" value={patientPublicId} />
      <input type="hidden" name="addressPublicId" value={addressPublicId} />

      {state.message && (
        <div
          role="alert"
          className="mb-5 rounded-xl border border-sand bg-sand/25 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      <p className="mb-4 text-sm leading-relaxed text-ink-muted">
        For an address that was written down wrong. If the family has moved, record a new
        address instead so this one is kept as where they used to live.
      </p>

      <AddressFields id={id} values={values} errors={state.errors} />

      <div className="mt-5">
        <SubmitButton label="Save correction" busy="Saving…" />
      </div>
    </form>
  );
}
