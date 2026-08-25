import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The caseload, waiting.
 *
 * Matches the real list: a divided `rounded-2xl` panel of rows, each an initials circle
 * beside a name and an age. The circle is `size-10`, the same as `PatientRow`'s, so the
 * rows do not change height when the list arrives.
 *
 * FIVE ROWS. The list is a whole caseload rather than a page of one, so the fallback is
 * long enough to fill a phone screen; a two-row placeholder followed by twenty patients
 * is a jump the skeleton existed to prevent.
 *
 * NO INITIALS IN THE CIRCLES and no name-shaped bars: a skeleton must be identical for
 * every caseload, and a placeholder that hinted at how many children the practice sees
 * would be disclosing something before the page had loaded.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading patients">
      <div className="flex flex-wrap items-center gap-4">
        <Skeleton className="h-9 w-36" />
        <Skeleton className="ml-auto h-10 w-32 rounded-full" />
      </div>

      <div className="mt-6 flex flex-wrap items-center gap-3">
        <Skeleton className="h-11 min-w-0 flex-1 rounded-xl" />
        <Skeleton className="h-5 w-36" />
        <Skeleton className="h-11 w-28 rounded-full" />
      </div>

      <ul className="mt-6 divide-y divide-ice overflow-hidden rounded-2xl border border-ice bg-white">
        {[0, 1, 2, 3, 4].map((row) => (
          <li key={row} className="flex items-center gap-4 px-5 py-4">
            <Skeleton className="size-10 shrink-0 rounded-full" />
            <span className="min-w-0 flex-1">
              <Skeleton className="h-4.5 w-48" />
              <Skeleton className="mt-2 h-3.5 w-28" />
            </span>
          </li>
        ))}
      </ul>
    </LoadingRegion>
  );
}
