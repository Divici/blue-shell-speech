"use client";

import { useActionState } from "react";
import { useFormStatus } from "react-dom";
import Link from "next/link";
import { submitConsultation } from "./actions";
import { INITIAL_CONSULTATION_STATE } from "./state";
import { PREFERRED_CONTACT_OPTIONS } from "@/lib/consultation-schema";
import { ArrowRightIcon, HeartCheckIcon } from "@/components/icons";

function SubmitButton() {
  const { pending } = useFormStatus();

  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex items-center gap-2 rounded-full bg-blue-action px-7 py-3.5 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending ? "Sending…" : "Send Request"}
      {!pending && <ArrowRightIcon size={18} />}
    </button>
  );
}

/** Shared field styling. Errors get a visible border, not colour alone. */
const fieldClass =
  "w-full rounded-xl border bg-white px-4 py-3 text-ink outline-none transition-colors placeholder:text-ink-muted/60";

function fieldBorder(hasError: boolean) {
  return hasError ? "border-coral" : "border-ice focus:border-blue";
}

export function ConsultationForm() {
  const [state, formAction] = useActionState(
    submitConsultation,
    INITIAL_CONSULTATION_STATE,
  );

  if (state.status === "success") {
    return (
      <div
        // Announced to screen readers when it replaces the form.
        role="status"
        className="rounded-3xl border border-ice bg-white p-8 text-center shadow-sm"
      >
        <span className="mx-auto grid size-14 place-items-center rounded-2xl bg-teal/15 text-teal">
          <HeartCheckIcon size={28} />
        </span>
        <h2 className="mt-4 font-display text-2xl font-bold text-navy">
          Thank you — your request is on its way.
        </h2>
        <p className="mx-auto mt-3 max-w-md text-ink-muted">
          Michelle will get back to you within two business days. If it’s easier to talk
          sooner, you’re welcome to call.
        </p>
        <Link
          href="/"
          className="mt-6 inline-flex items-center gap-2 rounded-full border border-ice px-5 py-3 text-sm font-semibold text-blue-deep"
        >
          Back to home
        </Link>
      </div>
    );
  }

  const hasErrors = Object.keys(state.errors).length > 0;

  return (
    <form action={formAction} noValidate className="rounded-3xl border border-ice bg-white p-6 shadow-sm sm:p-8">
      {hasErrors && (
        <div
          role="alert"
          className="mb-6 rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm text-navy"
        >
          Please check the highlighted fields below.
        </div>
      )}

      <div className="grid gap-5 sm:grid-cols-2">
        <Field
          name="parentName"
          label="Your name"
          autoComplete="name"
          error={state.errors.parentName}
          required
        />
        <Field
          name="email"
          label="Email"
          type="email"
          autoComplete="email"
          error={state.errors.email}
          required
        />
        <Field
          name="phone"
          label="Phone"
          type="tel"
          autoComplete="tel"
          hint="Optional unless you'd prefer a call."
          error={state.errors.phone}
        />
        <div>
          <label htmlFor="preferredContact" className="mb-1.5 block text-sm font-semibold text-navy">
            How should we reach you? <Required />
          </label>
          <select
            id="preferredContact"
            name="preferredContact"
            defaultValue="Either"
            aria-invalid={Boolean(state.errors.preferredContact)}
            aria-describedby={state.errors.preferredContact ? "preferredContact-error" : undefined}
            className={`${fieldClass} ${fieldBorder(Boolean(state.errors.preferredContact))}`}
          >
            {PREFERRED_CONTACT_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
          <FieldError id="preferredContact-error" message={state.errors.preferredContact} />
        </div>

        <Field
          name="childFirstName"
          label="Your child's first name"
          error={state.errors.childFirstName}
          required
        />
        <Field
          name="childAgeMonths"
          label="Your child's age in months"
          type="number"
          inputMode="numeric"
          min={0}
          hint="For example, 30 for a two-and-a-half-year-old."
          error={state.errors.childAgeMonths}
          required
        />
      </div>

      <div className="mt-5">
        <label htmlFor="concerns" className="mb-1.5 block text-sm font-semibold text-navy">
          What are you noticing? <Required />
        </label>
        <textarea
          id="concerns"
          name="concerns"
          rows={5}
          maxLength={2000}
          aria-invalid={Boolean(state.errors.concerns)}
          aria-describedby={state.errors.concerns ? "concerns-error" : "concerns-hint"}
          className={`${fieldClass} ${fieldBorder(Boolean(state.errors.concerns))} resize-y`}
        />
        <p id="concerns-hint" className="mt-1.5 text-sm text-ink-muted">
          There’s no wrong answer here — a sentence or two is plenty.
        </p>
        <FieldError id="concerns-error" message={state.errors.concerns} />
      </div>

      {/* Honeypot. Hidden from people and from assistive tech; bots fill it anyway. */}
      <div aria-hidden="true" className="absolute left-[-9999px] h-0 w-0 overflow-hidden">
        <label htmlFor="website">Leave this field empty</label>
        <input id="website" name="website" type="text" tabIndex={-1} autoComplete="off" />
      </div>

      <div className="mt-7 flex flex-wrap items-center gap-4">
        <SubmitButton />
        <p className="text-sm text-ink-muted">
          We’ll only use this to get back to you about therapy.
        </p>
      </div>
    </form>
  );
}

function Required() {
  return (
    <>
      <span aria-hidden="true" className="text-coral">
        *
      </span>
      <span className="sr-only">(required)</span>
    </>
  );
}

function FieldError({ id, message }: { id: string; message?: string | undefined }) {
  if (!message) return null;
  return (
    <p id={id} className="mt-1.5 text-sm font-medium text-navy">
      {message}
    </p>
  );
}

interface FieldProps {
  name: string;
  label: string;
  type?: string;
  hint?: string;
  error?: string | undefined;
  required?: boolean;
  autoComplete?: string;
  inputMode?: "numeric";
  min?: number;
}

function Field({ name, label, type = "text", hint, error, required, ...rest }: FieldProps) {
  const errorId = `${name}-error`;
  const hintId = `${name}-hint`;

  return (
    <div>
      <label htmlFor={name} className="mb-1.5 block text-sm font-semibold text-navy">
        {label} {required && <Required />}
      </label>
      <input
        id={name}
        name={name}
        type={type}
        aria-invalid={Boolean(error)}
        aria-describedby={error ? errorId : hint ? hintId : undefined}
        className={`${fieldClass} ${fieldBorder(Boolean(error))}`}
        {...rest}
      />
      {hint && !error && (
        <p id={hintId} className="mt-1.5 text-sm text-ink-muted">
          {hint}
        </p>
      )}
      <FieldError id={errorId} message={error} />
    </div>
  );
}
