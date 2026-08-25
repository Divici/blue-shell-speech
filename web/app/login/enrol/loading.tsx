import { ShellMark } from "@/components/brand/ShellMark";
import { LoadingRegion } from "@/components/loading/LoadingRegion";
import { Skeleton } from "@/components/loading/Skeleton";

/**
 * First-run MFA enrolment, waiting.
 *
 * THE SLOWEST STEP OF SIGN-IN. This page calls `/auth/mfa/enrol/begin` and then renders a
 * QR code from the shared secret it returns — an API round trip plus SVG generation, on
 * the one screen a provider reaches while holding a phone and expecting something to scan.
 *
 * The QR placeholder is a 180px square, matching the rendered code exactly, so the card
 * does not grow by that much when it arrives.
 */
export default function Loading() {
  return (
    <main
      id="main"
      className="flex min-h-dvh flex-col items-center justify-center bg-mist px-4 py-16"
    >
      <LoadingRegion label="Preparing two-factor setup" className="w-full max-w-sm">
        <div className="mx-auto mb-8 flex w-fit items-center gap-2.5">
          <ShellMark size={40} />
        </div>

        <div className="rounded-3xl border border-ice bg-white p-7 shadow-sm">
          <Skeleton className="h-7 w-full" />
          <Skeleton className="mt-3 h-4 w-full" />
          <Skeleton className="mt-1.5 h-4 w-4/5" />

          <Skeleton className="mx-auto mt-6 size-[180px] rounded-xl" />

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
