import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton, SkeletonCard, SkeletonText } from "@/components/loading/Skeleton";

/**
 * One enquiry, waiting.
 *
 * This is the screen that discloses what a parent wrote about their child, and the read
 * behind it is the one the API audits (D065). The wait is therefore a real API round trip
 * every time — there is nothing cached to fall back on — so the fallback matters here more
 * than on a page that could have been prerendered.
 *
 * The layout is the real one: a two-column panel of the parent's account beside an aside
 * holding contact details, with the next-steps card below.
 *
 * THE PARENT'S WORDS ARE THE ONE THING A PLACEHOLDER MUST NOT IMITATE. Four constant-width
 * lines, identical for a two-sentence enquiry and a five-paragraph one.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading the enquiry">
      <Skeleton className="h-4 w-24" />

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <Skeleton className="h-9 w-56" />
        <Skeleton className="h-6 w-24 rounded-full" />
      </div>
      <Skeleton className="mt-2 h-4 w-72" />

      <div className="mt-8 grid gap-6 lg:grid-cols-3">
        <SkeletonCard className="lg:col-span-2">
          <Skeleton className="h-6 w-48" />
          <Skeleton className="mt-2 h-3.5 w-40" />
          <SkeletonText className="mt-5" lines={4} />
        </SkeletonCard>

        <aside className="space-y-6">
          <SkeletonCard>
            <Skeleton className="h-5 w-40" />
            <SkeletonText className="mt-4" lines={3} />
          </SkeletonCard>
        </aside>
      </div>

      <SkeletonCard className="mt-6">
        <Skeleton className="h-5 w-28" />
        <div className="mt-5 flex flex-wrap items-center gap-3">
          <Skeleton className="h-11 w-36 rounded-full" />
          <Skeleton className="h-11 w-28 rounded-full" />
        </div>
      </SkeletonCard>
    </LoadingRegion>
  );
}
