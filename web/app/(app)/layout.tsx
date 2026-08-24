import { redirect } from "next/navigation";
import Link from "next/link";
import { ShellMark } from "@/components/brand/ShellMark";
import { getSession } from "@/lib/auth/session";
import { signOut } from "../login/actions";

/**
 * The authenticated shell.
 *
 * `force-dynamic` here covers EVERY route in this group, so no authenticated page can be
 * statically rendered or cached by forgetting a per-page directive. docs/THREAT_MODEL.md
 * ranks "PHI cached at a CDN edge" as the single most likely accidental disclosure in the
 * system, and the framework default leans toward caching.
 *
 * The session check is also here rather than repeated per page — a route added later is
 * protected by existing in this group.
 */
export const dynamic = "force-dynamic";

export default async function AppLayout({ children }: LayoutProps<"/">) {
  const session = await getSession();

  // Server-side. Hiding navigation is not authorization (CLAUDE.md non-negotiable #6);
  // the API re-checks ownership on every request regardless.
  if (!session) redirect("/login");

  return (
    <div className="min-h-dvh bg-mist">
      <header className="border-b border-ice bg-white">
        <div className="mx-auto flex max-w-5xl items-center gap-5 px-4 py-3 sm:px-6">
          <Link href="/dashboard" className="flex items-center gap-2.5">
            <ShellMark size={30} />
            <span className="font-display text-lg font-bold text-navy">Blue Shell</span>
          </Link>

          <nav aria-label="Practice" className="flex items-center gap-4">
            <Link href="/dashboard" className="text-sm font-medium text-ink hover:text-blue-deep">
              Dashboard
            </Link>
            <Link href="/patients" className="text-sm font-medium text-ink hover:text-blue-deep">
              Patients
            </Link>
          </nav>

          <div className="ml-auto flex items-center gap-4">
            <span className="hidden text-sm text-ink-muted sm:inline">
              {session.displayName}
            </span>
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

      <main id="main" className="mx-auto max-w-5xl px-4 py-8 sm:px-6">{children}</main>
    </div>
  );
}
