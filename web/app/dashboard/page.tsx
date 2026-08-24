import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { ShellMark } from "@/components/brand/ShellMark";
import { getSession } from "@/lib/auth/session";
import { signOut } from "../login/actions";

export const metadata: Metadata = {
  title: "Dashboard",
  robots: { index: false, follow: false },
};

/**
 * The authenticated shell.
 *
 * `force-dynamic` is not optional here. This page renders PHI-adjacent content behind a
 * cookie, and a cached response served to the next visitor is a disclosure — ranked the
 * single most likely accidental leak in docs/THREAT_MODEL.md.
 */
export const dynamic = "force-dynamic";

export default async function DashboardPage() {
  const session = await getSession();

  // Server-side. Hiding the UI is not authorization (CLAUDE.md non-negotiable #6).
  if (!session) redirect("/login");

  return (
    <div className="min-h-dvh bg-mist">
      <header className="border-b border-ice bg-white">
        <div className="mx-auto flex max-w-5xl items-center gap-4 px-4 py-3 sm:px-6">
          <ShellMark size={32} />
          <span className="font-display text-lg font-bold text-navy">Blue Shell Speech</span>

          <div className="ml-auto flex items-center gap-4">
            <span className="text-sm text-ink-muted">{session.displayName}</span>
            <form action={signOut}>
              <button
                type="submit"
                className="rounded-full border border-ice px-4 py-2 text-sm font-medium text-ink-muted hover:border-blue hover:text-blue-deep"
              >
                Sign out
              </button>
            </form>
          </div>
        </div>
      </header>

      <main id="main" className="mx-auto max-w-5xl px-4 py-10 sm:px-6">
        <h1 className="font-display text-3xl font-bold text-navy">
          Welcome back, {session.displayName}.
        </h1>
        <p className="mt-2 text-ink-muted">
          Patients, scheduling, and clinical notes arrive in the next slices.
        </p>
      </main>
    </div>
  );
}
