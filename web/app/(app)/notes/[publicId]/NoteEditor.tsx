"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { saveDraft, signNote, amendNote, discardDraft } from "./actions";
import { INITIAL_NOTE_STATE } from "./state";
import type { ClinicalNote } from "@/lib/api/notes";
import { parseApiInstant } from "@/lib/practice-time";

const SECTIONS = [
  {
    name: "subjective",
    label: "Subjective",
    hint: "What the family reported, and what the child said or did.",
  },
  {
    name: "objective",
    label: "Objective",
    hint: "What you observed and measured — percentages, trials, cue levels.",
  },
  {
    name: "assessment",
    label: "Assessment",
    hint: "Your clinical interpretation of the above.",
  },
  { name: "plan", label: "Plan", hint: "What happens next session." },
] as const;

function ActionButton({
  label,
  pendingLabel,
  variant = "primary",
}: {
  label: string;
  pendingLabel: string;
  variant?: "primary" | "secondary";
}) {
  const { pending } = useFormStatus();
  const styles =
    variant === "primary"
      ? "bg-blue-action text-white hover:opacity-90"
      : "border border-ice text-blue-deep hover:border-blue";

  return (
    <button
      type="submit"
      disabled={pending}
      className={`rounded-full px-6 py-3 font-semibold transition-opacity disabled:opacity-70 ${styles}`}
    >
      {pending ? pendingLabel : label}
    </button>
  );
}

export function NoteEditor({ note }: { note: ClinicalNote }) {
  return note.status === "Draft" ? <DraftEditor note={note} /> : <SignedNote note={note} />;
}

/**
 * Nothing written in any of the four sections, and superseding nothing.
 *
 * The same question ClinicalNote.CanBeDiscarded asks in the aggregate and
 * TR_ClinicalNotes_PreventDeletingRealNotes asks in the database — whitespace included,
 * because UpdateContent trims before storing. This layer only decides whether to offer
 * the control; the two below decide whether the row goes.
 *
 * `isAmendment` is checked FIRST because an amendment satisfies every other clause. It
 * starts as a Draft, clearing it is an ordinary edit, and the copy beneath this control
 * would then claim nothing has been saved in the note while a signed version sits under
 * it marked Amended. Leading a clinician to a tap the API refuses is worse than not
 * offering it: the screen has told her something untrue about her own record.
 */
function isEmptyNote(note: ClinicalNote): boolean {
  if (note.isAmendment) return false;
  return SECTIONS.every((section) => !note[section.name].trim());
}

function DraftEditor({ note }: { note: ClinicalNote }) {
  const [saveState, saveAction] = useActionState(saveDraft, INITIAL_NOTE_STATE);
  const [signState, signAction] = useActionState(signNote, INITIAL_NOTE_STATE);
  const [discardState, discardAction] = useActionState(discardDraft, INITIAL_NOTE_STATE);

  const state = signState.status === "idle" ? saveState : signState;
  const values = state.values ?? note;

  return (
    <div className="mt-6">
      {state.status === "error" && (
        <div
          role="alert"
          className="mb-6 rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      {state.status === "saved" && (
        <div
          role="status"
          className="mb-6 rounded-xl border border-teal/40 bg-teal/10 px-4 py-3 text-sm text-navy"
        >
          Draft saved.
        </div>
      )}

      {/*
        ONE form, two actions.

        The sign button posts to a different action via `formAction`, so it submits the
        same fields the clinician is looking at. Signing whatever was last persisted —
        rather than what is on screen — would attest to something they did not see.
      */}
      <form action={saveAction} className="rounded-2xl border border-ice bg-white p-6 sm:p-8">
        <input type="hidden" name="publicId" value={note.publicId} />

        <div className="space-y-6">
          {SECTIONS.map((section) => (
            <div key={section.name}>
              <label
                htmlFor={section.name}
                className="mb-1.5 block text-sm font-semibold text-navy"
              >
                {section.label}
              </label>
              <textarea
                id={section.name}
                name={section.name}
                rows={4}
                defaultValue={values[section.name]}
                className="w-full rounded-xl border border-ice bg-white px-4 py-3 leading-relaxed text-ink outline-none focus:border-blue"
              />
              <p className="mt-1.5 text-sm text-ink-muted">{section.hint}</p>
            </div>
          ))}
        </div>

        <div className="mt-8 flex flex-wrap items-center gap-4">
          <ActionButton label="Save draft" pendingLabel="Saving…" variant="secondary" />
          <button
            type="submit"
            formAction={signAction}
            className="rounded-full bg-blue-action px-6 py-3 font-semibold text-white transition-opacity hover:opacity-90"
          >
            Sign note
          </button>
          <p className="text-sm text-ink-muted">
            Signing is final. Corrections afterwards create a new version.
          </p>
        </div>
      </form>

      {/*
        The way out of a mis-tap, offered only while there is nothing to lose.

        A SEPARATE <form>, so discarding never carries the editor's textareas with it and
        the two submit buttons above keep posting only what they mean to.

        The consequence sits next to the control rather than behind a confirmation dialog,
        the way closing a goal does (D063) — a dialog people click through is a decision
        that was never made. It disappears the moment anything is saved into the note,
        because from then on the API and the database both refuse the delete.
      */}
      {isEmptyNote(note) && (
        <form action={discardAction} className="mt-6">
          <input type="hidden" name="publicId" value={note.publicId} />

          {discardState.status === "error" && (
            <div
              role="alert"
              className="mb-4 rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm text-navy"
            >
              {discardState.message}
            </div>
          )}

          <p className="text-sm text-ink-muted">
            Nothing has been saved in this note yet. Discarding it removes it from the
            visit, which then reads as undocumented again.
          </p>

          <button
            type="submit"
            className="mt-3 text-sm font-semibold text-blue-deep underline underline-offset-4 hover:text-navy"
          >
            Discard this empty note
          </button>
        </form>
      )}
    </div>
  );
}

function SignedNote({ note }: { note: ClinicalNote }) {
  const [amendState, amendAction] = useActionState(amendNote, INITIAL_NOTE_STATE);
  const [amending, setAmending] = useState(false);

  return (
    <div className="mt-6">
      <article className="rounded-2xl border border-ice bg-white p-6 sm:p-8">
        {note.amendmentReason && (
          <p className="mb-5 rounded-xl bg-mist px-4 py-3 text-sm text-navy">
            <strong>Amendment reason:</strong> {note.amendmentReason}
          </p>
        )}

        <dl className="space-y-5">
          {SECTIONS.map((section) => (
            <div key={section.name}>
              <dt className="text-sm font-semibold text-navy">{section.label}</dt>
              <dd className="mt-1 whitespace-pre-wrap leading-relaxed text-ink">
                {note[section.name] || <span className="text-ink-muted">Not recorded.</span>}
              </dd>
            </div>
          ))}
        </dl>

        {note.signedAtUtc && (
          <p className="mt-7 border-t border-ice pt-5 text-sm text-ink-muted">
            Signed by <strong className="text-navy">{note.signedBy}</strong> on{" "}
            {new Intl.DateTimeFormat("en-US", {
              dateStyle: "long",
              timeStyle: "short",
              timeZone: "America/New_York",
            }).format(parseApiInstant(note.signedAtUtc))}
          </p>
        )}
      </article>

      <div className="mt-6">
        {amendState.status === "error" && (
          <div
            role="alert"
            className="mb-4 rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm text-navy"
          >
            {amendState.message}
          </div>
        )}

        {amending ? (
          <form action={amendAction} className="rounded-2xl border border-ice bg-white p-6">
            <input type="hidden" name="publicId" value={note.publicId} />

            <label htmlFor="reason" className="mb-1.5 block text-sm font-semibold text-navy">
              Why does this note need correcting?
            </label>
            <input
              id="reason"
              name="reason"
              autoFocus
              className="w-full rounded-xl border border-ice bg-white px-4 py-3 text-ink outline-none focus:border-blue"
            />
            <p className="mt-1.5 text-sm text-ink-muted">
              This becomes part of the permanent record, and the current version is kept in
              full.
            </p>

            <div className="mt-5 flex items-center gap-4">
              <ActionButton label="Start amendment" pendingLabel="Starting…" />
              <button
                type="button"
                onClick={() => setAmending(false)}
                className="text-sm font-medium text-ink-muted hover:text-blue-deep"
              >
                Cancel
              </button>
            </div>
          </form>
        ) : (
          <button
            type="button"
            onClick={() => setAmending(true)}
            className="rounded-full border border-ice bg-white px-5 py-2.5 text-sm font-semibold text-blue-deep hover:border-blue"
          >
            Amend this note
          </button>
        )}
      </div>
    </div>
  );
}
