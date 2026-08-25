import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton, SkeletonCard, SkeletonText } from "@/components/loading/Skeleton";

/**
 * A patient record, waiting.
 *
 * THE SLOWEST SCREEN IN THE APPLICATION and therefore the one that most needs this: the
 * page awaits the record and the goals list together, so it is two API round trips wide
 * even when they run in parallel.
 *
 * The three-column grid is reproduced exactly — a two-column clinical summary beside an
 * aside holding the contact and address cards — because that grid is what changes the
 * page's height most when it resolves. Below it, the goals panel and the guardian and
 * address sections keep their `mt-6` rhythm.
 *
 * NO NAME-SHAPED HEADING. The real `h1` is the child's first and last name; a placeholder
 * whose width tracked it would leak the length of a patient's name into a frame that is on
 * screen in a family's living room. Every bar here is a constant.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading the patient record">
      <Skeleton className="h-4 w-24" />

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <Skeleton className="h-9 w-64" />
      </div>
      <Skeleton className="mt-2 h-4 w-52" />

      <div className="mt-8 grid gap-6 lg:grid-cols-3">
        <SkeletonCard className="lg:col-span-2">
          <Skeleton className="h-6 w-44" />
          <SkeletonText className="mt-4" lines={4} />
        </SkeletonCard>

        <aside className="space-y-6">
          <SkeletonCard>
            <Skeleton className="h-5 w-24" />
            <SkeletonText className="mt-4" lines={3} />
          </SkeletonCard>

          <SkeletonCard>
            <Skeleton className="h-5 w-40" />
            <SkeletonText className="mt-4" lines={3} />
          </SkeletonCard>
        </aside>
      </div>

      <SkeletonCard className="mt-6">
        <Skeleton className="h-6 w-20" />

        <ul className="mt-5 space-y-4">
          {[0, 1].map((goal) => (
            <li key={goal} className="rounded-xl border border-ice p-5">
              <Skeleton className="h-4 w-28" />
              <SkeletonText className="mt-3" lines={2} />
              <div className="mt-4 flex flex-wrap gap-3">
                <Skeleton className="h-10 w-28 rounded-full" />
                <Skeleton className="h-10 w-32 rounded-full" />
              </div>
            </li>
          ))}
        </ul>
      </SkeletonCard>

      <SkeletonCard className="mt-6">
        <Skeleton className="h-6 w-32" />
        <SkeletonText className="mt-4" lines={3} />
      </SkeletonCard>

      <SkeletonCard className="mt-6">
        <Skeleton className="h-6 w-28" />
        <SkeletonText className="mt-4" lines={3} />
      </SkeletonCard>
    </LoadingRegion>
  );
}
