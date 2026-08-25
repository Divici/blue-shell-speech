import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The daily view, waiting.
 *
 * SHAPED LIKE THE REAL SCREEN because the real screen is known: a heading, the day
 * controls, and a column of visit cards. `VisitCard` is a `rounded-2xl border p-5` list
 * item whose fixed rows are the time, the patient's name, and the note control under a
 * divider — so the placeholder holds the same height and content lands in the space
 * already reserved for it rather than shoving the page down (CLS ≤ 0.1).
 *
 * THREE CARDS, NOT THE REAL COUNT, which is not knowable until the day arrives. Three is
 * roughly a morning, so the fallback is close to full-height on a phone and the scroll
 * position does not lurch when six visits replace it.
 *
 * NOTHING HERE IS DERIVED FROM A PATIENT. The bars are constant widths; a placeholder
 * sized to a real name would leak the length of it before the record has loaded, and the
 * screen would look different for a caseload of one child than for forty.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading the day's visits">
      <div className="flex flex-wrap items-center gap-3">
        <Skeleton className="h-9 w-40" />
        <Skeleton className="h-10 w-36 rounded-full" />

        <div className="ml-auto flex items-center gap-2">
          <Skeleton className="h-9 w-14 rounded-full" />
          <Skeleton className="h-9 w-20 rounded-full" />
          <Skeleton className="h-9 w-14 rounded-full" />
        </div>
      </div>

      <ul className="mt-8 space-y-4">
        {[0, 1, 2].map((row) => (
          <li key={row} className="rounded-2xl border border-ice bg-white p-5">
            <div className="flex flex-wrap items-baseline gap-x-4">
              <Skeleton className="h-6 w-44" />
              <Skeleton className="ml-auto h-4 w-24" />
            </div>

            <Skeleton className="mt-3 h-6 w-52" />
            <Skeleton className="mt-3 h-4 w-40" />

            <div className="mt-4 border-t border-ice pt-4">
              <Skeleton className="h-10 w-32 rounded-full" />
            </div>
          </li>
        ))}
      </ul>
    </LoadingRegion>
  );
}
