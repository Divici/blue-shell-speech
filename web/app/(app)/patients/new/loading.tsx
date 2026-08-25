import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The add-patient form, waiting.
 *
 * This page fetches nothing itself — so the wait it covers is the framework's, not an
 * API's: `app/(app)/layout.tsx` forces dynamic rendering for every route behind the login,
 * and the segment still has to be rendered and shipped before anything paints. On a
 * container that has scaled to zero that is the 22-second cold start, on a page whose
 * whole content is a form.
 *
 * A skeleton rather than a spinner for the same reason as the booking form: the shape is
 * fixed and known, so there is a layout worth reserving.
 */
export default function Loading() {
  return (
    <LoadingRegion label="Loading the patient form">
      <Skeleton className="h-4 w-24" />
      <Skeleton className="mt-3 h-9 w-52" />
      <Skeleton className="mt-3 h-4 w-96 max-w-full" />

      <div className="mt-6 rounded-2xl border border-ice bg-white p-6 sm:p-8">
        <div className="grid gap-5 sm:grid-cols-2">
          {[0, 1, 2, 3].map((field) => (
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
