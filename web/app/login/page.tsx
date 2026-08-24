import type { Metadata } from "next";
import Link from "next/link";
import { redirect } from "next/navigation";
import { ShellMark } from "@/components/brand/ShellMark";
import { getSession } from "@/lib/auth/session";
import { LoginForm } from "./LoginForm";

export const metadata: Metadata = {
  title: "Provider Login",
  // Nothing behind authentication should be indexed, and a login page has no reason to
  // appear in search results for a practice whose patients never sign in.
  robots: { index: false, follow: false },
};

/**
 * Sign-in, step one.
 *
 * Dynamic and never cached: it reads a cookie, and a cached authenticated redirect served
 * to the next visitor would be a session leak.
 */
export const dynamic = "force-dynamic";

export default async function LoginPage() {
  if (await getSession()) {
    redirect("/dashboard");
  }

  return (
    <main id="main" className="flex min-h-dvh flex-col items-center justify-center bg-mist px-4 py-16">
      <div className="w-full max-w-sm">
        <Link href="/" className="mx-auto mb-8 flex w-fit items-center gap-2.5">
          <ShellMark size={40} />
          <span className="font-display text-xl font-bold text-navy">
            Blue Shell
            <span className="block font-sans text-[0.62rem] font-semibold uppercase tracking-[0.28em] text-blue-deep">
              Speech
            </span>
          </span>
        </Link>

        <div className="rounded-3xl border border-ice bg-white p-7 shadow-sm">
          <h1 className="font-display text-2xl font-bold text-navy">Provider sign-in</h1>
          <p className="mt-1.5 mb-6 text-sm text-ink-muted">
            This area is for the practice. Parents can{" "}
            <Link href="/consultation" className="font-medium text-blue-deep hover:underline">
              request a consultation
            </Link>
            .
          </p>

          <LoginForm />
        </div>

        <p className="mt-6 text-center text-xs text-ink-muted">
          Protected by two-factor authentication.
        </p>
      </div>
    </main>
  );
}
