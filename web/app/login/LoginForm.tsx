"use client";

import { useActionState } from "react";
import { useFormStatus } from "react-dom";
import { signIn } from "./actions";
import { INITIAL_LOGIN_STATE } from "./state";

function SubmitButton() {
  const { pending } = useFormStatus();
  return (
    <button
      type="submit"
      disabled={pending}
      className="w-full rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
    >
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
