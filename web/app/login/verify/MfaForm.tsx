"use client";

import { useActionState, useState } from "react";
import { useFormStatus } from "react-dom";
import { Spinner } from "@/components/loading/Spinner";
import { verifyMfa } from "../actions";
import { INITIAL_MFA_STATE } from "../state";

function SubmitButton({ label }: { label: string }) {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex w-full items-center justify-center gap-2.5 rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending && <Spinner size={18} />}
      {pending ? "Verifying…" : label}
    </button>
  );
}

export function MfaForm() {
  const [state, formAction] = useActionState(verifyMfa, INITIAL_MFA_STATE);
  const [useRecovery, setUseRecovery] = useState(false);

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

      <input type="hidden" name="mode" value={useRecovery ? "recovery" : "totp"} />

      <div>
        <label htmlFor="code" className="mb-1.5 block text-sm font-semibold text-navy">
          {useRecovery ? "Recovery code" : "Six-digit code"}
        </label>
        <input
          id="code"
          name="code"
          type="text"
          required
          autoFocus
          autoComplete="one-time-code"
          /* Numeric keypad on a phone for TOTP; recovery codes contain letters. */
          inputMode={useRecovery ? "text" : "numeric"}
          /* Never store or suggest a second factor. */
          spellCheck={false}
          className="w-full rounded-xl border border-ice bg-white px-4 py-3 text-center text-lg tracking-[0.3em] text-ink outline-none focus:border-blue"
        />
      </div>

      <SubmitButton label={useRecovery ? "Use recovery code" : "Verify"} />

      <button
        type="button"
        onClick={() => setUseRecovery((v) => !v)}
        className="w-full text-center text-sm font-medium text-blue-deep hover:underline"
      >
        {useRecovery
          ? "Use my authenticator app instead"
          : "I can't reach my authenticator app"}
      </button>
    </form>
  );
}
