import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The dashboard, waiting.
 *
 * THIS IS THE SCREEN THE REPORTED BUG LANDS ON. Sign-in ends in `redirect("/dashboard")`,
 * and this page counts the caseload — so the last step of authentication is an API round
 * trip against a container that has very likely scaled to zero. Without a fallback the
 * browser holds the verification screen, unchanged and unexplained, for the whole cold
 * start.
 *
 * Three tiles, matching the real `sm:grid-cols-3`. Two of them are static placards
 * ("scheduling arrives in slice 4") that need no data, but drawing only the first would
 * leave the row reflowing from one column to three when it resolves.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading your dashboard">
      <Skeleton className="h-9 w-80" />

      <div className="mt-8 grid gap-4 sm:grid-cols-3">
        {[0, 1, 2].map((tile) => (
          <div key={tile} className="rounded-2xl border border-ice bg-white p-6">
            <Skeleton className="h-9 w-16" />
            <Skeleton className="mt-2 h-4 w-28" />
          </div>
        ))}
      </div>
    </LoadingRegion>
  );
}
