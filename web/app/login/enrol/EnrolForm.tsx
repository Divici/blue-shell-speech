"use client";

import { useActionState } from "react";
import { useFormStatus } from "react-dom";
import { Spinner } from "@/components/loading/Spinner";
import Link from "next/link";
import { completeEnrolment } from "../actions";
import { INITIAL_MFA_STATE } from "../state";

function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex w-full items-center justify-center gap-2.5 rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending && <Spinner size={18} />}
      {pending ? "Confirming…" : "Confirm and finish setup"}
    </button>
  );
}

interface EnrolFormProps {
  qrSvg: string;
  sharedKey: string;
}

export function EnrolForm({ qrSvg, sharedKey }: EnrolFormProps) {
  const [state, formAction] = useActionState(completeEnrolment, INITIAL_MFA_STATE);

  /*
   * Recovery codes, shown exactly once.
   *
   * They are stored hashed and cannot be retrieved again. That is correct, and it has to
   * be unmistakable here — a clinician who closes this screen without saving them can be
   * locked out of every patient record in the practice by a lost phone.
   */
  if (state.status === "enrolled") {
    return (
      <div role="status" className="space-y-5">
        <div className="rounded-xl border border-sand bg-sand/25 px-4 py-3 text-sm text-navy">
          <strong>Save these now.</strong> They will not be shown again, and each one works
          only once. Keep them somewhere separate from your phone.
        </div>

        <ul className="grid grid-cols-2 gap-2 rounded-xl bg-mist p-4 font-mono text-sm text-ink">
          {state.recoveryCodes?.map((code) => (
            <li key={code}>{code}</li>
          ))}
        </ul>

        <Link
          href="/login"
          className="block w-full rounded-full bg-blue-action px-6 py-3.5 text-center font-semibold text-white"
        >
          I&rsquo;ve saved them — continue to sign in
        </Link>
      </div>
    );
  }

  return (
    <form action={formAction} className="space-y-5">
      {state.status === "error" && (
        <div
          role="alert"
          className="rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm text-navy"
        >
          {state.message}
        </div>
      )}

      {/*
        The QR is rendered to SVG on the server, so the secret never passes through a
        client-side QR library and no third-party script sees it.
      */}
      <div
        className="mx-auto w-fit rounded-xl border border-ice bg-white p-3"
        dangerouslySetInnerHTML={{ __html: qrSvg }}
      />

      <details className="text-sm text-ink-muted">
        <summary className="cursor-pointer font-medium text-blue-deep">
          Can&rsquo;t scan the code?
        </summary>
        <p className="mt-2">Enter this key into your authenticator app by hand:</p>
        <code className="mt-1 block break-all rounded-lg bg-mist px-3 py-2 font-mono text-xs text-ink">
          {sharedKey}
        </code>
      </details>

      <div>
        <label htmlFor="code" className="mb-1.5 block text-sm font-semibold text-navy">
          Six-digit code from the app
        </label>
        <input
          id="code"
          name="code"
          type="text"
          required
          inputMode="numeric"
          autoComplete="one-time-code"
          spellCheck={false}
          className="w-full rounded-xl border border-ice bg-white px-4 py-3 text-center text-lg tracking-[0.3em] text-ink outline-none focus:border-blue"
        />
      </div>

      <SubmitButton />
    </form>
  );
}
