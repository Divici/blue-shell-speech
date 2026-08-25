import { ShellMark } from "@/components/brand/ShellMark";
import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * Step two of sign-in, waiting.
 *
 * THIS IS HALF OF THE REPORTED BUG. `signIn` ends in `redirect("/login/verify")`, and the
 * password form's own pending state — measured, and it works — stops at the moment the
 * action resolves. What follows is a navigation to a `force-dynamic` page on an origin
 * that may still be waking, with nothing on screen to say so. The clinician sees the
 * button return to "Sign in" and then nothing happens for as long as the cold start takes,
 * which reads exactly like a tap that did not register.
 *
 * The page itself only decrypts a cookie, so on a warm container this fallback is
 * invisible. It exists for the cold one, which is the case that was reported.
 */
export default function Loading() {
  return (
    <main
      id="main"
      className="flex min-h-dvh flex-col items-center justify-center bg-mist px-4 py-16"
    >
      <LoadingRegion label="Loading two-factor verification" className="w-full max-w-sm">
        <div className="mx-auto mb-8 flex w-fit items-center gap-2.5">
          <ShellMark size={40} />
        </div>

        <div className="rounded-3xl border border-ice bg-white p-7 shadow-sm">
          <Skeleton className="h-7 w-44" />
          <Skeleton className="mt-3 h-4 w-full" />

          <div className="mt-6 space-y-5">
            <Skeleton className="h-4 w-24" />
            <Skeleton className="h-12 w-full rounded-xl" />
            <Skeleton className="h-13 w-full rounded-full" />
          </div>
        </div>
      </LoadingRegion>
    </main>
  );
}
