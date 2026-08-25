import { redirect } from "next/navigation";
import Link from "next/link";
import { ShellMark } from "@/components/brand/ShellMark";
import { getSession } from "@/lib/auth/session";
import { SignOutButton } from "./SignOutButton";

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
            <Link href="/today" className="text-sm font-medium text-ink hover:text-blue-deep">
              Today
            </Link>
            <Link href="/patients" className="text-sm font-medium text-ink hover:text-blue-deep">
              Patients
            </Link>
            {/*
              The destination "New consultation request, sign in to view" points at. The
              notification carries no content by design (D079), so the only way to find out
              what arrived is to come here — which means it has to be reachable from every
              authenticated screen rather than from a link in an email.
            */}
            <Link href="/enquiries" className="text-sm font-medium text-ink hover:text-blue-deep">
              Enquiries
            </Link>
          </nav>

          <div className="ml-auto flex items-center gap-4">
            <span className="hidden text-sm text-ink-muted sm:inline">
              {session.displayName}
            </span>
            {/*
              A Client Component for the pending state alone. Everything else in this
              header stays on the server, including the clinician name beside it.
            */}
            <SignOutButton />
          </div>
        </div>
      </header>

      <main id="main" className="mx-auto max-w-5xl px-4 py-8 sm:px-6">{children}</main>
    </div>
  );
}
