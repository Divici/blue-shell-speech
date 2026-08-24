import type { Metadata } from "next";
import Link from "next/link";
import { SiteHeader } from "@/components/marketing/SiteHeader";
import { SiteFooter } from "@/components/marketing/SiteFooter";
import { ShellMark } from "@/components/brand/ShellMark";

export const metadata: Metadata = {
  title: "Provider Login",
  description: "Sign in to the Blue Shell Speech practice application.",
  // Nothing behind this route should be indexed, and the login page itself has no
  // reason to appear in search results for a practice whose patients never sign in.
  robots: { index: false, follow: false },
};

/**
 * Placeholder for the provider login.
 *
 * Authentication ships in slice 2 (ASP.NET Core Identity, mandatory TOTP MFA). This page
 * exists now because the header links to it, and a 404 from a live site's own navigation
 * looks like a broken deployment rather than an unfinished feature.
 *
 * Deliberately says nothing about how authentication will work, whether an account
 * exists, or what lies behind it — a login page is an enumeration surface, and this one
 * has exactly one real user.
 */
export default function LoginPage() {
  return (
    <>
      <SiteHeader />
      <main id="main" className="bg-mist">
        <div className="mx-auto flex min-h-[60vh] max-w-md flex-col items-center justify-center px-4 py-20 text-center sm:px-6">
          <ShellMark size={56} />
          <h1 className="mt-6 font-display text-3xl font-bold text-navy">Provider login</h1>
          <p className="mt-3 text-ink-muted">
            Sign-in isn&rsquo;t available yet. If you&rsquo;re a parent looking to get in
            touch, the consultation form is the right place to start.
          </p>
          <Link
            href="/consultation"
            className="mt-7 inline-flex items-center gap-2 rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90"
          >
            Request a Free Consultation
          </Link>
          <Link href="/" className="mt-4 text-sm font-medium text-blue-deep hover:underline">
            Back to home
          </Link>
        </div>
      </main>
      <SiteFooter />
    </>
  );
}
