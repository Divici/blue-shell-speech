import type { Metadata } from "next";
import { redirect } from "next/navigation";
import QRCode from "qrcode";
import { ShellMark } from "@/components/brand/ShellMark";
import { authApi } from "@/lib/auth/api-client";
import { getPendingMfa, getSession } from "@/lib/auth/session";
import { EnrolForm } from "./EnrolForm";

export const metadata: Metadata = {
  title: "Set Up Two-Factor Authentication",
  robots: { index: false, follow: false },
};

export const dynamic = "force-dynamic";

/**
 * First-run MFA enrolment.
 *
 * There is no way past this screen. A provider with a correct password but no second
 * factor is sent here and nowhere else — MFA is mandatory, so "skip for now" does not
 * exist (docs/SECURITY.md).
 */
export default async function EnrolPage() {
  if (await getSession()) redirect("/dashboard");

  const pending = await getPendingMfa();
  if (!pending) redirect("/login");

  const enrolment = await authApi.beginEnrolment(pending.userId);

  /*
   * Rendered to SVG server-side, never a remote image service.
   *
   * A hosted QR generator would receive the TOTP shared secret in the URL — handing the
   * second factor to a third party, in a request log, in plaintext.
   */
  const qrSvg = await QRCode.toString(enrolment.authenticatorUri, {
    type: "svg",
    margin: 0,
    width: 180,
    color: { dark: "#1B4FA3", light: "#FFFFFF" },
  });

  return (
    <main id="main" className="flex min-h-dvh flex-col items-center justify-center bg-mist px-4 py-16">
      <div className="w-full max-w-sm">
        <div className="mx-auto mb-8 flex w-fit items-center gap-2.5">
          <ShellMark size={40} />
        </div>

        <div className="rounded-3xl border border-ice bg-white p-7 shadow-sm">
          <h1 className="font-display text-2xl font-bold text-navy">
            Set up two-factor authentication
          </h1>
          <p className="mt-1.5 mb-6 text-sm text-ink-muted">
            Scan this with an authenticator app — Google Authenticator, 1Password, or
            similar. This protects every patient record in the practice, so it is required.
          </p>

          <EnrolForm qrSvg={qrSvg} sharedKey={enrolment.sharedKey} />
        </div>
      </div>
    </main>
  );
}
