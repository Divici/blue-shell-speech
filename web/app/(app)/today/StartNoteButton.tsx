"use client";

import { useActionState } from "react";
import { startNote } from "./actions";
import { INITIAL_START_NOTE_STATE } from "./state";

/**
 * Starts the clinical note for a visit that has none.
 *
 * A real <form> posting to a server action, so it works before hydration and without
 * JavaScript — the schedule is read on a phone with one bar, and "tap and nothing
 * happens" is the worst failure this screen has.
 *
 * The React 19 form reset does not bite here: the only field is a hidden input whose
 * value comes from a prop, so there is nothing a post-action reset could discard. Any
 * user-editable field added to this form would need the remount-on-echo treatment the
 * consultation and scheduling forms use.
 */
export function StartNoteButton({
  visitPublicId,
  patientName,
}: {
  visitPublicId: string;
  patientName: string;
}) {
  const [state, formAction, pending] = useActionState(startNote, INITIAL_START_NOTE_STATE);

  return (
    <form action={formAction}>
      <input type="hidden" name="visitPublicId" value={visitPublicId} />

      {/*
        aria-label rather than an appended sr-only span.

        Every card carries the same words, so the visible label alone leaves a screen
        reader announcing a column of identical buttons. The name-computation algorithm
        joins adjacent nodes without a separator — "Start notefor Maya" — so the label is
        written out in full here instead. The visible text is a prefix of it, which is
        what WCAG 2.5.3 (Label in Name) requires.
      */}
      <button
        type="submit"
        disabled={pending}
        aria-label={
          pending ? `Starting note for ${patientName}` : `Start note for ${patientName}`
        }
        className="rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
      >
        {pending ? "Starting…" : "Start note"}
      </button>

      {state.status === "error" && (
        <p
          role="alert"
          className="mt-2 rounded-xl border border-coral bg-coral/10 px-3 py-2 text-sm text-navy"
        >
          {state.message}
        </p>
      )}
    </form>
  );
}
