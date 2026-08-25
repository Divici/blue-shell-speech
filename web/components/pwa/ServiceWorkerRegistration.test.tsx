import { describe, it, expect, vi, afterEach } from "vitest";
import { render, waitFor, act } from "@testing-library/react";
import { ServiceWorkerRegistration } from "./ServiceWorkerRegistration";

/**
 * The one place the worker is registered.
 *
 * It renders nothing, which is the point: it exists because a service worker has to be
 * registered by a script running on a page, and every other candidate for that job — a
 * `<script>` in the layout, a `useEffect` bolted onto a real component — either needs a
 * CSP nonce or ties the lifetime of the worker to the lifetime of some unrelated UI.
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("<ServiceWorkerRegistration />", () => {
  /**
   * Control: the `registerServiceWorker()` call inside the effect.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" to be called with arguments:
   * [ '/sw.js', { scope: '/' } ]", with "Number of calls: 0".
   */
  it("registers the worker once mounted", async () => {
    const register = vi.fn().mockResolvedValue({ scope: "https://example.test/" });
    vi.stubGlobal("navigator", { serviceWorker: { register } });

    render(<ServiceWorkerRegistration />);

    await waitFor(() => expect(register).toHaveBeenCalledWith("/sw.js", { scope: "/" }));
  });

  /**
   * It renders nothing, in a layout wrapping every page including the marketing site.
   *
   * Control: the `return null` in `ServiceWorkerRegistration`.
   * Replaced with a `<div />` → red, "AssertionError: expected '<div></div>' to be ''".
   */
  it("renders nothing", () => {
    vi.stubGlobal("navigator", { serviceWorker: { register: vi.fn().mockResolvedValue({}) } });

    const { container } = render(<ServiceWorkerRegistration />);

    expect(container.innerHTML).toBe("");
  });

  /**
   * Once per mount, not once per render.
   *
   * This component sits in the root layout, so it re-renders on every navigation and every
   * state change anywhere above it. `register()` is idempotent in the browser, but calling
   * it on every render turns a one-off into a per-navigation round trip on a container
   * that scales to zero.
   *
   * IT REPLACED A TEST THAT WAS GREEN BY CONSTRUCTION. The original third case here was
   * "does not throw where service workers do not exist", asserting
   * `expect(() => render(...)).not.toThrow()`. The effect calls `void
   * registerServiceWorker()`, so a failure inside it is an unhandled promise rejection and
   * never a synchronous throw — the assertion could not see the defect it named, and
   * stayed green with BOTH controls in `lib/pwa/register.ts` neutered at once. That claim
   * is falsifiable one file over and is asserted there instead (D066's class, found by
   * running the deletion rather than reading the assertion).
   *
   * Control: the `[]` dependency array on the effect in `ServiceWorkerRegistration`.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" to be called once, but got 2
   * times".
   */
  it("registers once per mount, not once per render", async () => {
    const register = vi.fn().mockResolvedValue({ scope: "https://example.test/" });
    vi.stubGlobal("navigator", { serviceWorker: { register } });

    const { rerender } = render(<ServiceWorkerRegistration />);
    await waitFor(() => expect(register).toHaveBeenCalled());

    await act(async () => {
      rerender(<ServiceWorkerRegistration />);
      rerender(<ServiceWorkerRegistration />);
    });

    expect(register).toHaveBeenCalledOnce();
  });
});
