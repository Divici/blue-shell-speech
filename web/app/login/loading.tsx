import { ShellMark } from "@/components/brand/ShellMark";
import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * The sign-in card, waiting.
 *
 * `/login` reads a cookie and is `force-dynamic`, so it is never prerendered and never
 * served from an edge cache — every arrival renders on the origin, and the origin scales
 * to zero. This is the first screen anybody sees.
 *
 * THE SHELL MARK IS REAL, not a grey box. It is a compile-time SVG constant with nothing
 * to wait for, and a practice logo on a waiting screen is the difference between "loading"
 * and "broken" for a clinician on a phone with one bar.
 */
export default function Loading() {
  return (
    <main
      id="main"
      className="flex min-h-dvh flex-col items-center justify-center bg-mist px-4 py-16"
    >
      <LoadingRegion label="Loading sign-in" className="w-full max-w-sm">
        <div className="mx-auto mb-8 flex w-fit items-center gap-2.5">
          <ShellMark size={40} />
        </div>

        <div className="rounded-3xl border border-ice bg-white p-7 shadow-sm">
          <Skeleton className="h-7 w-48" />
          <Skeleton className="mt-3 h-4 w-full" />

          <div className="mt-6 space-y-5">
            {[0, 1].map((field) => (
              <div key={field}>
                <Skeleton className="h-4 w-20" />
                <Skeleton className="mt-1.5 h-12 w-full rounded-xl" />
              </div>
            ))}
            <Skeleton className="h-13 w-full rounded-full" />
          </div>
        </div>
      </LoadingRegion>
    </main>
  );
}
