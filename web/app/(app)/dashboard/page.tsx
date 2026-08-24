import type { Metadata } from "next";
import Link from "next/link";
import { getSession } from "@/lib/auth/session";
import { patientsApi } from "@/lib/api/patients";

export const metadata: Metadata = {
  title: "Dashboard",
  robots: { index: false, follow: false },
};

export default async function DashboardPage() {
  // The layout already guarantees a session; this is for the greeting.
  const session = await getSession();
  const patients = await patientsApi.list();

  const active = patients.filter((p) => p.status === "Active").length;

  return (
    <>
      <h1 className="font-display text-3xl font-bold text-navy">
        Welcome back, {session?.displayName}.
      </h1>

      <div className="mt-8 grid gap-4 sm:grid-cols-3">
        <Link
          href="/patients"
          className="rounded-2xl border border-ice bg-white p-6 transition-colors hover:border-blue"
        >
          <p className="font-display text-3xl font-bold text-navy">{active}</p>
          <p className="mt-1 text-sm text-ink-muted">
            active {active === 1 ? "patient" : "patients"}
          </p>
        </Link>

        <div className="rounded-2xl border border-dashed border-ice p-6 text-ink-muted">
          <p className="text-sm">Scheduling arrives in slice 4.</p>
        </div>

        <div className="rounded-2xl border border-dashed border-ice p-6 text-ink-muted">
          <p className="text-sm">Dictation arrives in slice 6.</p>
        </div>
      </div>
    </>
  );
}
