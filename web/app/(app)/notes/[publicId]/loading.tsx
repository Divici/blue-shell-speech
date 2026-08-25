import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * A clinical note, waiting.
 *
 * FOUR SECTIONS, AND THE LABELS ARE REAL. Subjective, Objective, Assessment and Plan are
 * constants of the note format rather than anything read from a record, so rendering them
 * costs nothing and tells the clinician which note she is about to see. The Next.js
 * loading guidance calls this out directly: prerender "a small but meaningful part of
 * future screens" rather than an anonymous rectangle.
 *
 * WHAT IS NOT RENDERED is anything under those labels. The section bodies are placeholder
 * bars sized to the editor's `rows={4}` textareas, so the content lands in a box that is
 * already the right height whether the note is empty or four paragraphs long — and a
 * clinician glancing at a phone never sees a shape that suggests how much was written
 * before the note itself arrives.
 */
const SECTIONS = ["Subjective", "Objective", "Assessment", "Plan"] as const;

export default function Loading() {
  return (
    <LoadingRegion label="Loading the session note">
      <Skeleton className="h-4 w-24" />

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <Skeleton className="h-9 w-52" />
        <Skeleton className="h-6 w-20 rounded-full" />
      </div>

      <div className="mt-6 rounded-2xl border border-ice bg-white p-6 sm:p-8">
        <div className="space-y-6">
          {SECTIONS.map((section) => (
            <div key={section}>
              <p className="mb-1.5 text-sm font-semibold text-navy">{section}</p>
              {/* The editor's textarea is rows={4} inside px-4 py-3 — this is that box. */}
              <Skeleton className="h-[7.75rem] w-full rounded-xl" />
              <Skeleton className="mt-2 h-3.5 w-3/5" />
            </div>
          ))}
        </div>

        <div className="mt-8 flex flex-wrap items-center gap-4">
          <Skeleton className="h-12 w-36 rounded-full" />
          <Skeleton className="h-12 w-32 rounded-full" />
        </div>
      </div>
    </LoadingRegion>
  );
}
