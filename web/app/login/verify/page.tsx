import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { ShellMark } from "@/components/brand/ShellMark";
import { getPendingMfa, getSession } from "@/lib/auth/session";
import { MfaForm } from "./MfaForm";

export const metadata: Metadata = {
  title: "Two-Factor Verification",
  robots: { index: false, follow: false },
};

export const dynamic = "force-dynamic";

/**
 * Sign-in, step two.
 *
 * Reachable only with a valid pending-MFA cookie. Landing here directly redirects to the
 * start — this page must never be a way to probe whether an account exists.
 */
export default async function VerifyPage() {
  if (await getSession()) redirect("/dashboard");
  if (!(await getPendingMfa())) redirect("/login");

  return (
    <main id="main" className="flex min-h-dvh flex-col items-center justify-center bg-mist px-4 py-16">
      <div className="w-full max-w-sm">
        <div className="mx-auto mb-8 flex w-fit items-center gap-2.5">
          <ShellMark size={40} />
        </div>

        <div className="rounded-3xl border border-ice bg-white p-7 shadow-sm">
          <h1 className="font-display text-2xl font-bold text-navy">Confirm it&rsquo;s you</h1>
          <p className="mt-1.5 mb-6 text-sm text-ink-muted">
            Open your authenticator app and enter the current code.
          </p>

          <MfaForm />
        </div>
      </div>
    </main>
  );
}
