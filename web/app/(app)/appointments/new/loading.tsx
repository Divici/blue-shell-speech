import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The booking form, waiting.
 *
 * The form itself needs no data, but the PATIENT PICKER does: the page fetches the whole
 * caseload on the server so the browser never receives an array of children's names. That
 * one call is what this waits on, and it is a full round trip to a container that may have
 * scaled to zero.
 *
 * The field grid is reproduced rather than replaced by a spinner because the shape IS
 * known here — it is a fixed form, not a list whose length depends on the answer. Content
 * lands in boxes already the right height.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading the booking form">
      <Skeleton className="h-4 w-24" />
      <Skeleton className="mt-3 h-9 w-56" />
      <Skeleton className="mt-3 h-4 w-96 max-w-full" />

      <div className="mt-6 rounded-2xl border border-ice bg-white p-6 sm:p-8">
        <div className="grid gap-5 sm:grid-cols-2">
          {[0, 1, 2, 3, 4, 5].map((field) => (
            <div key={field}>
              <Skeleton className="h-4 w-28" />
              <Skeleton className="mt-1.5 h-12 w-full rounded-xl" />
            </div>
          ))}
        </div>

        <Skeleton className="mt-8 h-12 w-36 rounded-full" />
      </div>
    </LoadingRegion>
  );
}
