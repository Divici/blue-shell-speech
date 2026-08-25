import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { notesApi, type ClinicalNote } from "@/lib/api/notes";
import { NoteEditor } from "./NoteEditor";

export const metadata: Metadata = {
  title: "Clinical Note",
  robots: { index: false, follow: false },
};

/**
 * A clinical note and its full version history.
 *
 * The history is shown, not hidden behind a toggle. An amended record where only the
 * latest version is visible is not an audit trail — and "what did the note say before it
 * was corrected" is the question an auditor, a lawyer, or a colleague actually asks.
 */
export default async function NotePage(props: PageProps<"/notes/[publicId]">) {
  const { publicId } = await props.params;
  const versions = await notesApi.history(publicId);

  if (versions.length === 0) notFound();

  const current = versions.find((v) => v.isCurrent) ?? versions[0]!;
  const superseded = versions.filter((v) => !v.isCurrent);

  return (
    <>
      <Link href="/today" className="text-sm font-medium text-blue-deep hover:underline">
        &larr; Schedule
      </Link>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <h1 className="font-display text-3xl font-bold text-navy">Session note</h1>
        <StatusBadge note={current} />
        {versions.length > 1 && (
          <span className="text-sm text-ink-muted">
            version {current.versionNumber} of {versions.length}
          </span>
        )}
      </div>

      {/*
        An integrity failure means the stored hash no longer matches the content — the row
        was altered by something that bypassed both the aggregate and the trigger. It is
        surfaced loudly because a silently corrupted clinical record is the worst possible
        outcome for this system.
      */}
      {!current.integrityVerified && (
        <div
          role="alert"
          className="mt-4 rounded-xl border-2 border-coral bg-coral/10 px-4 py-3 text-navy"
        >
          <strong>Integrity check failed.</strong> The stored signature does not match this
          content. Do not rely on this note — report it before continuing.
        </div>
      )}

      <NoteEditor note={current} />

      {superseded.length > 0 && (
        <section className="mt-10">
          <h2 className="font-display text-xl font-bold text-navy">Previous versions</h2>
          <p className="mt-1 text-sm text-ink-muted">
            Retained in full. Nothing here was overwritten.
          </p>

          <ol className="mt-5 space-y-5">
            {superseded
              .sort((a, b) => b.versionNumber - a.versionNumber)
              .map((version) => (
                <li
                  key={version.publicId}
                  className="rounded-2xl border border-ice bg-white/70 p-5"
                >
                  <div className="flex flex-wrap items-center gap-3">
                    <span className="font-semibold text-navy">
                      Version {version.versionNumber}
                    </span>
                    {version.signedAtUtc && (
                      <span className="text-sm text-ink-muted">
                        signed by {version.signedBy}
                      </span>
                    )}
                    {!version.integrityVerified && (
                      <span className="rounded-full bg-coral/20 px-2.5 py-0.5 text-xs font-semibold text-navy">
                        integrity failed
                      </span>
                    )}
                  </div>

                  <ReadOnlyNote note={version} />
                </li>
              ))}
          </ol>
        </section>
      )}
    </>
  );
}

function StatusBadge({ note }: { note: ClinicalNote }) {
  const styles =
    note.status === "Signed"
      ? "bg-teal/15 text-teal"
      : note.status === "Draft"
        ? "bg-sand/40 text-navy"
        : "bg-ice text-blue-deep";

  return (
    <span className={`rounded-full px-3 py-1 text-xs font-semibold ${styles}`}>
      {note.status}
    </span>
  );
}

function ReadOnlyNote({ note }: { note: ClinicalNote }) {
  const sections = [
    ["Subjective", note.subjective],
    ["Objective", note.objective],
    ["Assessment", note.assessment],
    ["Plan", note.plan],
  ] as const;

  return (
    <>
      {note.amendmentReason && (
        <p className="mt-3 text-sm text-ink-muted">
          <strong className="text-navy">Amendment reason:</strong> {note.amendmentReason}
        </p>
      )}

      <dl className="mt-3 space-y-3">
        {sections
          .filter(([, text]) => text.trim())
          .map(([label, text]) => (
            <div key={label}>
              <dt className="text-sm font-semibold text-navy">{label}</dt>
              <dd className="whitespace-pre-wrap text-sm leading-relaxed text-ink">{text}</dd>
            </div>
          ))}
      </dl>
    </>
  );
}
