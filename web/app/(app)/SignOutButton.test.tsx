import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

/**
 * The one control that appears on every authenticated screen.
 *
 * It destroys a session against a container that scales to zero, so the wait is real and
 * the consequence of a second press is a second POST to `/auth` — a path that is rate
 * limited (D098) and that a clinician handing her phone to a parent needs a definite
 * answer from. Before this, the button said "Sign out" throughout and stayed pressable.
 */

vi.mock("server-only", () => ({}));
vi.mock("../login/actions", () => ({ signOut: vi.fn() }));

import { SignOutButton } from "./SignOutButton";
import { signOut } from "../login/actions";

describe("SignOutButton", () => {
  beforeEach(() => vi.clearAllMocks());

  /**
   * Held until released — an unresolved action outlives the unmount and breaks the NEXT
   * test rather than this one, which is how it was found in `NoteEditor.test.tsx`.
   */
  function holdable() {
    let release: () => void = () => {};
    let first = true;

    const impl = () => {
      if (!first) return Promise.resolve(undefined);
      first = false;
      return new Promise<undefined>((resolve) => {
        release = () => resolve(undefined);
      });
    };

    return { impl, release: () => release() };
  }

  async function settle(held: { release: () => void }) {
    held.release();
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /^sign out$/i })).toBeEnabled(),
    );
  }

  /**
   * Control: the `disabled={pending}` attribute in `SignOutButton.Button`.
   * Deleted → red, "expect(element).toBeDisabled() … Received element is not disabled".
   */
  it("disables itself while the session is being destroyed", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signOut).mockImplementation(held.impl);

    render(<SignOutButton />);
    await user.click(screen.getByRole("button", { name: /^sign out$/i }));

    expect(screen.getByRole("button", { name: /signing out/i })).toBeDisabled();
    await settle(held);
  });

  /**
   * Control: the `pending ? "Signing out…" : "Sign out"` expression.
   * Reduced to the plain label → red, "Unable to find an accessible element with the role
   * \"button\" and name `/signing out/i`".
   */
  it("says what it is doing rather than only greying out", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signOut).mockImplementation(held.impl);

    render(<SignOutButton />);
    await user.click(screen.getByRole("button", { name: /^sign out$/i }));

    expect(screen.getByRole("button", { name: /signing out/i })).toBeInTheDocument();
    await settle(held);
  });

  /**
   * Behaviour rather than an attribute: the requirement is that a second POST is not
   * reachable, and React DEFERS a queued submission rather than dropping it — so the
   * count is only meaningful after the first has resolved.
   *
   * Control: the `disabled={pending}` attribute.
   * Deleted → red in `settle`, because the queued second submission re-enters the pending
   * state the moment the first resolves and the label never returns to "Sign out".
   */
  it("cannot be pressed twice", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signOut).mockImplementation(held.impl);

    render(<SignOutButton />);
    const button = screen.getByRole("button", { name: /^sign out$/i });

    await user.click(button);
    await user.click(button);
    await user.click(button);

    await settle(held);

    expect(vi.mocked(signOut)).toHaveBeenCalledTimes(1);
  });
});
