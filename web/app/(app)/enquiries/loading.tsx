import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The consultation inbox, waiting.
 *
 * THE FIVE FILTER PILLS ARE PLACEHOLDERS, NOT LINKS. Rendering the real tabs here would
 * put controls on screen that navigate away from a page still arriving, and the selected
 * one is decided by the search parameter this render is waiting on — so a skeleton that
 * guessed would highlight the wrong tab and then correct itself.
 *
 * Five rows, matching the real divided panel: a parent's name over a child line, with the
 * wait and a status badge right-aligned. Row heights match `EnquiryRow` so nothing shifts.
 *
 * NOTHING HERE IS A FAMILY. No name-shaped bars, no counts — this frame is identical for
 * an empty inbox and a busy one, which is both the anti-CLS property and the privacy one.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading enquiries">
      <div className="flex flex-wrap items-center gap-3">
        <Skeleton className="h-9 w-44" />
      </div>

      <div className="mt-6 flex flex-wrap gap-2">
        {[0, 1, 2, 3, 4].map((tab) => (
          <Skeleton key={tab} className="h-10 w-28 rounded-full" />
        ))}
      </div>

      <ul className="mt-6 divide-y divide-ice overflow-hidden rounded-2xl border border-ice bg-white">
        {[0, 1, 2, 3, 4].map((row) => (
          <li key={row} className="flex flex-wrap items-center gap-x-4 gap-y-2 px-5 py-4">
            <span className="min-w-0 flex-1">
              <Skeleton className="h-4.5 w-44" />
              <Skeleton className="mt-2 h-3.5 w-60" />
            </span>

            <span className="text-right">
              <Skeleton className="ml-auto h-4.5 w-24" />
              <Skeleton className="mt-1.5 ml-auto h-6 w-20 rounded-full" />
            </span>
          </li>
        ))}
      </ul>
    </LoadingRegion>
  );
}
