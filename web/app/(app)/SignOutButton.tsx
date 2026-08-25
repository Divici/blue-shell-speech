"use client";

import { useFormStatus } from "react-dom";
import { Spinner } from "@/components/loading/Spinner";
import { signOut } from "../login/actions";

/**
 * Sign out, with something to say while it happens.
 *
 * IT IS ON EVERY AUTHENTICATED SCREEN, so it is the control most likely to be pressed
 * twice: it sits in the layout, it is small, and it destroys a session against a container
 * that may have scaled to zero. Two POSTs is not dangerous — the second finds no cookie —
 * but a clinician handing a phone to somebody has to be able to tell whether she is signed
 * out yet, and a button that does not change tells her nothing.
 *
 * `useFormStatus` rather than `useActionState`, because `signOut` takes no previous state:
 * it is a bare server action that ends in `redirect("/login")` and has no return value to
 * thread through a reducer. The pending flag has to come from the form.
 *
 * A CLIENT COMPONENT, and only this button. The layout around it stays a Server Component
 * — it reads the session cookie and renders the clinician's name, neither of which should
 * cross into a browser bundle.
 */
function Button() {
  const { pending } = useFormStatus();

  return (
    <button
      type="submit"
      disabled={pending}
      className="inline-flex items-center gap-2 rounded-full border border-ice px-4 py-2 text-sm font-medium text-ink-muted hover:border-blue hover:text-blue-deep disabled:opacity-70"
    >
      {pending && <Spinner size={14} />}
      {pending ? "Signing out…" : "Sign out"}
    </button>
  );
}

export function SignOutButton() {
  return (
    <form action={signOut}>
      <Button />
    </form>
  );
}
