"use client";

import { useActionState } from "react";
import { useFormStatus } from "react-dom";
import { Spinner } from "@/components/loading/Spinner";
import { signIn } from "./actions";
import { INITIAL_LOGIN_STATE } from "./state";

/**
 * The control the reported bug was pinned on, and it was not the culprit.
 *
 * MEASURED, NOT ASSUMED: `e2e/loading.spec.ts` holds `/auth/password` open against a real
 * server and watches this button disable and relabel in all three engines. What was
 * missing was everything after it — `signIn` ends in a redirect, and `/login/verify`,
 * `/login/enrol` and `/dashboard` had no route-level fallback, so the wait that felt dead
 * was the one on the far side of this form. Those have `loading.tsx` now.
 *
 * The spinner is here for one reason beyond decoration: this is a wide primary button on a
 * near-empty screen, and `disabled:opacity-70` on a blue field is a change a person on a
 * phone in daylight can genuinely miss. The label is what a screen reader gets; the ring is
 * what an eye catches.
 *
 * `useFormStatus` rather than `useActionState`'s flag, because this form has ONE action.
 * The note editor uses the per-action flags instead, and for a stated reason: it has three.
 */
function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex w-full items-center justify-center gap-2.5 rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
      {pending && <Spinner size={18} />}
      {pending ? "Signing in…" : "Sign in"}
    </button>
  );
}

export function LoginForm() {
  const [state, formAction] = useActionState(signIn, INITIAL_LOGIN_STATE);

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

      <div>
        <label htmlFor="email" className="mb-1.5 block text-sm font-semibold text-navy">
          Email
        </label>
        <input
          id="email"
          name="email"
          type="email"
          autoComplete="username"
          required
          className="w-full rounded-xl border border-ice bg-white px-4 py-3 text-ink outline-none focus:border-blue"
        />
      </div>

      <div>
        <label htmlFor="password" className="mb-1.5 block text-sm font-semibold text-navy">
          Password
        </label>
        <input
          id="password"
          name="password"
          type="password"
          autoComplete="current-password"
          required
          className="w-full rounded-xl border border-ice bg-white px-4 py-3 text-ink outline-none focus:border-blue"
        />
      </div>

      <SubmitButton />
    </form>
  );
}
