"use client";

import Link from "next/link";
import { useActionState } from "react";
import type { EnquiryDetail } from "@/lib/api/enquiries";
import { Spinner } from "@/components/loading/Spinner";
import { formatAgeMonths } from "@/lib/age";
import { convertToPatient, declineEnquiry, markContacted } from "./actions";
import {
  INITIAL_CONVERT_STATE,
  INITIAL_ENQUIRY_TRANSITION_STATE,
  type ConvertState,
} from "./state";

/**
 * What to do next with an enquiry.
 *
 * Real <form>s posting to server actions, so every move works before hydration and without
 * JavaScript — this is read on a phone in a car with one bar, like the rest of the
 * authenticated app.
 *
 * IT OFFERS ONLY THE MOVES THE API WILL ACCEPT, and that is a courtesy rather than a
 * control: the endpoint refuses a closed enquiry whatever the browser sends
 * (RefusalToTransition), and hiding a button is not authorization (CLAUDE.md
 * non-negotiable #6). What hiding buys is not walking a clinician into a refusal she could
 * not have known about — the copy defect D076 records, on the other side of the same
 * boundary.
 *
 * NOTHING HERE DELETES. Declining is a transition; the enquiry stays exactly as the parent
 * wrote it.
 */
export function EnquiryActions({ enquiry }: { enquiry: EnquiryDetail }) {
  const [contactState, contactAction, contacting] = useActionState(
    markContacted,
    INITIAL_ENQUIRY_TRANSITION_STATE,
  );
  const [declineState, declineAction, declining] = useActionState(
    declineEnquiry,
    INITIAL_ENQUIRY_TRANSITION_STATE,
  );
  const [convertState, convertAction, converting] = useActionState(
    convertToPatient,
    INITIAL_CONVERT_STATE,
  );

  const busy = contacting || declining || converting;

  const problem =
    contactState.status === "error"
      ? contactState.message
      : declineState.status === "error"
        ? declineState.message
        : convertState.status === "error"
          ? convertState.message
          : null;

  /*
   * CLOSED IS CLOSED, and the two closed states read differently.
   *
   * A converted enquiry has a child on the caseload behind it, so the useful thing on this
   * panel is the way to that record. A declined one has nothing to go to; what it needs to
   * say is that the row is kept as it was.
   */
  if (enquiry.status === "Converted") {
    return (
      <section className="rounded-2xl border border-ice bg-white p-6">
        <h2 className="font-display text-lg font-bold text-navy">This family joined the practice</h2>
        <p className="mt-2 text-sm text-ink-muted">
          The enquiry is kept exactly as it arrived, and nothing further is recorded against
          it.
        </p>
        {enquiry.convertedPatientPublicId && (
          <Link
            href={`/patients/${enquiry.convertedPatientPublicId}`}
            className="mt-5 inline-block rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90"
          >
            Open patient record
          </Link>
        )}
      </section>
    );
  }

  if (enquiry.status === "Declined") {
    return (
      <section className="rounded-2xl border border-ice bg-white p-6">
        <h2 className="font-display text-lg font-bold text-navy">Declined</h2>
        <p className="mt-2 text-sm text-ink-muted">
          This enquiry is kept as it arrived and cannot be reopened. If the family gets in
          touch again, they can send a new request from the website.
        </p>
      </section>
    );
  }

  return (
    <section className="rounded-2xl border border-ice bg-white p-6">
      <h2 className="font-display text-lg font-bold text-navy">Next steps</h2>

      <div className="mt-4 flex flex-wrap items-center gap-3">
        {/*
          Only on an enquiry nobody has answered. The aggregate treats a second reply as
          the same state, so offering it again would be a button that changes nothing.
        */}
        {enquiry.status === "New" && (
          <form action={contactAction}>
            <EnquiryId publicId={enquiry.publicId} />
            <button
              type="submit"
              disabled={busy}
              className="inline-flex items-center gap-2 rounded-full bg-teal px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
            >
              {contacting && <Spinner size={14} />}
              {contacting ? "Saving…" : "Mark contacted"}
            </button>
          </form>
        )}

        <form action={declineAction}>
          <EnquiryId publicId={enquiry.publicId} />
          <button
            type="submit"
            disabled={busy}
            className="inline-flex items-center gap-2 rounded-full border border-ice bg-white px-5 py-2.5 text-sm font-semibold text-ink-muted transition-colors hover:border-blue hover:text-blue-deep disabled:opacity-70"
          >
            {declining && <Spinner size={14} />}
            {declining ? "Saving…" : "Decline"}
          </button>
        </form>

        {/*
          Stated in advance rather than confirmed afterwards, the same reasoning as closing
          a goal (D063): the API refuses to reopen a declined enquiry, so this is something
          the clinician needs to know BEFORE she taps. A dialog after the fact does not
          achieve that, and a disclosure would hide the controls this card exists for.
        */}
        <p className="text-sm text-ink-muted">
          A declined enquiry is kept on the record and cannot be reopened.
        </p>
      </div>

      {problem && (
        <p
          role="alert"
          className="mt-4 rounded-xl border border-coral bg-coral/10 px-3 py-2 text-sm text-navy"
        >
          {problem}
        </p>
      )}

      <ConvertForm
        enquiry={enquiry}
        action={convertAction}
        state={convertState}
        busy={busy}
        converting={converting}
      />
    </section>
  );
}

/**
 * Turning the enquiry into a patient record.
 *
 * TWO FIELDS, AND NO FIRST NAME. The public form asks for a child's first name and an age
 * in months and nothing else, so a surname and a date of birth have to be typed — and the
 * API takes the first name off the enquiry row rather than from this form. An input here
 * would be a value the browser chose for a field the enquiry already answers; the name is
 * shown as text so the clinician can see what she is about to create.
 *
 * The date of birth is asked for rather than derived from the age the parent gave. Age in
 * months is what every clinical decision in this practice is measured against, and a
 * birthdate computed from a rounded estimate is a number nobody stated.
 */
function ConvertForm({
  enquiry,
  action,
  state,
  busy,
  converting,
}: {
  enquiry: EnquiryDetail;
  action: (formData: FormData) => void;
  state: ConvertState;
  /** Anything on the panel is in flight — no move may start on top of another. */
  busy: boolean;
  /** THIS move is in flight, which is what the label is allowed to say. */
  converting: boolean;
}) {
  return (
    <form action={action} className="mt-6 border-t border-ice pt-6">
      <EnquiryId publicId={enquiry.publicId} />

      <h3 className="font-semibold text-navy">Create a patient record</h3>
      <p className="mt-1 text-sm text-ink-muted">
        For <strong className="font-semibold text-navy">{enquiry.childFirstName}</strong>,
        aged {formatAgeMonths(enquiry.childAgeMonths)}. The website never asked for a
        surname or a date of birth, so add them here.
      </p>

      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        <Field
          name="lastName"
          label="Last name"
          defaultValue={state.values?.lastName ?? ""}
          error={state.errors.lastName}
        />
        <Field
          name="dateOfBirth"
          label="Date of birth"
          type="date"
          defaultValue={state.values?.dateOfBirth ?? ""}
          error={state.errors.dateOfBirth}
        />
      </div>

      {/*
        THE SLOWEST MOVE ON THIS PANEL and the one that was silent while it ran: it was
        disabled during the write but never relabelled, so the card dimmed and said
        nothing. Creating a patient record from an enquiry writes a chart — the answer to
        "did my tap register" cannot be left to the opacity of a button.
      */}
      <button
        type="submit"
        disabled={busy}
        className="mt-5 inline-flex items-center gap-2 rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
      >
        {converting && <Spinner size={14} />}
        {converting ? "Creating record…" : "Create patient record"}
      </button>
    </form>
  );
}

function Field({
  name,
  label,
  type = "text",
  defaultValue,
  error,
}: {
  name: string;
  label: string;
  type?: string;
  defaultValue: string;
  error?: string | undefined;
}) {
  const errorId = `${name}-error`;

  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium text-ink">
        {label}
      </label>
      <input
        id={name}
        name={name}
        type={type}
        defaultValue={defaultValue}
        required
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className="mt-1 w-full rounded-xl border border-ice bg-white px-3 py-2.5 text-ink outline-none focus:border-blue"
      />
      {error && (
        <p id={errorId} className="mt-1 text-sm text-navy">
          {error}
        </p>
      )}
    </div>
  );
}

/**
 * The enquiry's id travels with every form.
 *
 * The actions read it from nowhere else, and the API re-checks ownership on every one of
 * them — an enquiry belonging to another provider answers 404, identically to one that
 * does not exist (D052). Nothing here is an authorization decision; it is addressing.
 */
function EnquiryId({ publicId }: { publicId: string }) {
  return <input type="hidden" name="publicId" value={publicId} />;
}
