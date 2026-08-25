"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { saveDraft, signNote, amendNote } from "./actions";
import { INITIAL_NOTE_STATE } from "./state";
import type { ClinicalNote } from "@/lib/api/notes";

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

function DraftEditor({ note }: { note: ClinicalNote }) {
  const [saveState, saveAction] = useActionState(saveDraft, INITIAL_NOTE_STATE);
  const [signState, signAction] = useActionState(signNote, INITIAL_NOTE_STATE);

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
            }).format(new Date(note.signedAtUtc))}
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
