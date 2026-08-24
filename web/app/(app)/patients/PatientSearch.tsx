"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useState, useTransition } from "react";

interface PatientSearchProps {
  defaultValue: string;
  includeDischarged: boolean;
}

/**
 * Search box.
 *
 * A Client Component only because it needs to push a URL. The RESULTS are rendered on the
 * server — no patient data is fetched by the browser, so nothing PHI-bearing lands in a
 * client cache or a devtools network log the clinician might screenshot.
 *
 * State lives in the URL, so a search is shareable, bookmarkable, and survives a refresh.
 */
export function PatientSearch({ defaultValue, includeDischarged }: PatientSearchProps) {
  const router = useRouter();
  const params = useSearchParams();
  const [term, setTerm] = useState(defaultValue);
  const [isPending, startTransition] = useTransition();

  const submit = (nextTerm: string, nextDischarged: boolean) => {
    const next = new URLSearchParams(params.toString());

    if (nextTerm.trim()) next.set("q", nextTerm.trim());
    else next.delete("q");

    if (nextDischarged) next.set("discharged", "1");
    else next.delete("discharged");

    startTransition(() => {
      router.push(next.toString() ? `/patients?${next}` : "/patients");
    });
  };

  return (
    <form
      role="search"
      onSubmit={(event) => {
        event.preventDefault();
        submit(term, includeDischarged);
      }}
      className="flex flex-wrap items-center gap-3"
    >
      <label htmlFor="patient-search" className="sr-only">
        Search patients by name
      </label>
      <input
        id="patient-search"
        type="search"
        value={term}
        onChange={(event) => setTerm(event.target.value)}
        placeholder="Search by name"
        className="min-w-0 flex-1 rounded-xl border border-ice bg-white px-4 py-2.5 text-ink outline-none focus:border-blue"
      />

      <label className="flex items-center gap-2 text-sm text-ink-muted">
        <input
          type="checkbox"
          checked={includeDischarged}
          onChange={(event) => submit(term, event.target.checked)}
          className="size-4 rounded border-ice"
        />
        Include discharged
      </label>

      <button
        type="submit"
        disabled={isPending}
        className="rounded-full border border-ice px-5 py-2.5 text-sm font-semibold text-blue-deep disabled:opacity-60"
      >
        {isPending ? "Searching…" : "Search"}
      </button>
    </form>
  );
}
