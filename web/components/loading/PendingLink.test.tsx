import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";

/**
 * Feedback for a navigation that looks like an in-page control.
 *
 * The status tabs and the day arrows are `<Link>`s on purpose — bookmarkable, back-button
 * friendly, functional without JavaScript. What they lacked was any sign of having been
 * pressed: the selected tab is derived from the search parameter the render is still
 * fetching, so during a cold start the OLD tab stays highlighted while the clinician looks
 * at a screen that appears not to have registered her tap.
 *
 * `useLinkStatus` is stubbed rather than driven through a real navigation. There is no
 * router in jsdom, so the hook would return `{ pending: false }` for ever and every
 * assertion here would be about nothing. What the stub leaves unproven — that Next
 * actually reports pending for these links — is proven against a real server in
 * `e2e/loading.spec.ts`.
 */

const linkStatus = { pending: false };

vi.mock("next/link", async () => {
  const actual = await vi.importActual<typeof import("next/link")>("next/link");
  return { ...actual, useLinkStatus: () => linkStatus };
});

import { PendingLink } from "./PendingLink";

const hint = (container: HTMLElement) => container.querySelector("[data-pending]");

describe("PendingLink", () => {
  beforeEach(() => {
    linkStatus.pending = false;
  });

  /**
   * It is still a link. Every property the tab strip was designed around — a real href, a
   * back button that works, a URL that can be bookmarked — depends on this, and the whole
   * point of adding feedback this way rather than with `router.push` was to keep it.
   *
   * Control: the `href` passed through to `<Link>` in `PendingLink`.
   * Deleted → the component does not compile; changed to a literal → red,
   * "expect(element).toHaveAttribute(\"href\", \"/enquiries?status=New\")".
   */
  it("is a real link to the destination", () => {
    render(
      <PendingLink href="/enquiries?status=New" className="tab">
        New
      </PendingLink>,
    );

    expect(screen.getByRole("link", { name: /new/i })).toHaveAttribute(
      "href",
      "/enquiries?status=New",
    );
  });

  /**
   * Control: the `pending` branch of the hint's className in `LinkPendingHint`.
   * Deleted (left permanently `opacity-0`) → red, "expect(element).toHaveClass(
   * \"opacity-100\")".
   */
  it("marks the link that was pressed while the navigation is in flight", () => {
    linkStatus.pending = true;

    const { container } = render(<PendingLink href="/today?date=2026-08-26">Next day</PendingLink>);

    expect(hint(container)).toHaveClass("opacity-100");
    expect(hint(container)).toHaveAttribute("data-pending", "true");
  });

  /**
   * NO LAYOUT SHIFT, AND NO SPACE EITHER.
   *
   * The first version reserved the dot's width inline, which does prevent a shift and is
   * what the Next.js documentation suggests — and it pushed the label of every tab about
   * six pixels left of centre, permanently, across a strip of five. Taking the dot out of
   * the flow gets the same guarantee and leaves the text where it was drawn.
   *
   * It is still rendered unconditionally, now for the fade rather than the layout: a
   * `transition-opacity` on an element that does not exist yet has nothing to animate from.
   *
   * Control: the `absolute` positioning on the hint.
   * Replaced with `inline-block` → red, "expect(element).toHaveClass(\"absolute\")" — and
   * the dot is back in the flow, widening every pill it is added to.
   */
  it("takes no space in the link's layout", () => {
    const { container } = render(<PendingLink href="/enquiries">All</PendingLink>);

    expect(hint(container)).toBeTruthy();
    expect(hint(container)).toHaveClass("absolute");
    expect(hint(container)).toHaveClass("opacity-0");
    // Positioned against the link itself, not against whatever ancestor happens to be
    // positioned — a dot in the corner of the page is the failure mode.
    expect(container.querySelector("a")).toHaveClass("relative");
  });

  /**
   * The dot says nothing, deliberately: the destination's `LoadingRegion` is the live
   * region for a navigation, and a second one here would announce the same wait twice in
   * less useful words.
   *
   * Control: `aria-hidden="true"` on the hint.
   * Deleted → red, "expect(element).toHaveAttribute(\"aria-hidden\", \"true\") … Received:
   * null".
   */
  it("adds nothing to the link's accessible name", () => {
    linkStatus.pending = true;

    const { container } = render(<PendingLink href="/enquiries?status=New">New</PendingLink>);

    expect(hint(container)).toHaveAttribute("aria-hidden", "true");
    expect(screen.getByRole("link", { name: "New" })).toBeInTheDocument();
  });

  /**
   * Reduced motion keeps the dot and drops the pulse. Hiding it under the preference would
   * remove the feedback from the people most likely to need a calm, unambiguous signal.
   *
   * Control: the `motion-reduce:animate-none` class on the pending hint.
   * Deleted → red, "expect(element).toHaveClass(\"motion-reduce:animate-none\")".
   */
  it("keeps the mark under prefers-reduced-motion", () => {
    linkStatus.pending = true;

    const { container } = render(<PendingLink href="/enquiries?status=New">New</PendingLink>);

    expect(hint(container)).toHaveClass("motion-reduce:animate-none");
    expect(hint(container)).toHaveClass("opacity-100");
  });
});
