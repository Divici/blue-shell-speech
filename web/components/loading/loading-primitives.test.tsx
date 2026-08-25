import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";

import { LoadingRegion } from "./LoadingRegion";
import { Skeleton, SkeletonText } from "./Skeleton";
import { Spinner } from "./Spinner";

/**
 * The pieces every loading screen in this application is assembled from.
 *
 * Two properties are asserted here rather than at each of the twelve call sites: that a
 * waiting screen says something to a screen reader, and that its motion can be turned off.
 * Both are the kind of thing that is correct in the component it was written for and
 * quietly absent from the eleven that copied it.
 */

describe("LoadingRegion", () => {
  /**
   * Control: `role="status"` on the wrapper in `LoadingRegion`.
   * Deleted → red, "Unable to find an accessible element with the role \"status\"".
   */
  it("is a status region a screen reader can find", () => {
    render(
      <LoadingRegion label="Loading today's visits">
        <Skeleton className="h-4" />
      </LoadingRegion>,
    );

    expect(screen.getByRole("status")).toBeInTheDocument();
  });

  /**
   * The skeleton is silent, so the region has to speak.
   *
   * Control: the `<span className="sr-only">{label}</span>` line in `LoadingRegion`.
   * Deleted → red, "Unable to find an element with the text: Loading today's visits".
   */
  it("names what is being waited for", () => {
    render(
      <LoadingRegion label="Loading today's visits">
        <Skeleton className="h-4" />
      </LoadingRegion>,
    );

    expect(screen.getByText("Loading today's visits")).toBeInTheDocument();
  });

  /**
   * Control: `aria-busy="true"` on the wrapper in `LoadingRegion`.
   * Deleted → red, "expect(element).toHaveAttribute(\"aria-busy\", \"true\") … Expected the
   * element to have attribute: aria-busy=\"true\" / Received: null".
   */
  it("marks itself busy while the segment streams", () => {
    render(
      <LoadingRegion label="Loading patients">
        <Skeleton className="h-4" />
      </LoadingRegion>,
    );

    expect(screen.getByRole("status")).toHaveAttribute("aria-busy", "true");
  });

  /**
   * ONE live region per screen, not one per rectangle.
   *
   * `role="status"` is an implicit `aria-live="polite"`, and nesting two of them makes
   * some screen readers announce the same wait twice. The skeleton bars are `aria-hidden`
   * for the same reason: forty of them read out individually is worse than silence.
   *
   * Control: `aria-hidden="true"` on the `<span>` in `Skeleton`.
   * Deleted → red, "expect(element).toHaveAttribute(\"aria-hidden\", \"true\") … Expected
   * the element to have attribute: aria-hidden=\"true\" / Received: null".
   */
  it("leaves the shapes inside it out of the accessibility tree", () => {
    const { container } = render(
      <LoadingRegion label="Loading patients">
        <SkeletonText lines={4} />
      </LoadingRegion>,
    );

    const bars = container.querySelectorAll(".animate-pulse");

    expect(bars.length).toBe(4);
    bars.forEach((bar) => expect(bar).toHaveAttribute("aria-hidden", "true"));
    expect(screen.getAllByRole("status")).toHaveLength(1);
  });
});

describe("reduced motion", () => {
  /**
   * `prefers-reduced-motion` is honoured at the element, not only in the stylesheet.
   *
   * jsdom applies no Tailwind, so this asserts the CLASS rather than a computed style —
   * stated plainly because a test that looks like it measures rendering and does not is
   * the defect docs/TEST_STRATEGY.md is about. What it does prove is that the opt-out was
   * not dropped from one of the two primitives, which is the way this regresses.
   * `app/globals.css` neutralises every animation under the same query as a backstop, and
   * its own coverage lives in the public-site suite.
   *
   * Control: the `motion-reduce:animate-none` class in `Skeleton`.
   * Deleted → red, "Expected the element to have class: motion-reduce:animate-none /
   * Received: block animate-pulse rounded-lg bg-ice h-4".
   */
  it("lets a skeleton stop pulsing", () => {
    const { container } = render(<Skeleton className="h-4" />);

    expect(container.firstElementChild).toHaveClass("motion-reduce:animate-none");
  });

  /**
   * Control: the `motion-reduce:animate-none` class in `Spinner`.
   * Deleted → red, "Expected the element to have class: motion-reduce:animate-none /
   * Received: animate-spin".
   */
  it("lets a spinner stop turning", () => {
    const { container } = render(<Spinner />);

    expect(container.firstElementChild).toHaveClass("motion-reduce:animate-none");
  });
});

describe("Spinner", () => {
  /**
   * The ring adds nothing to a button's accessible name — the label beside it, already
   * rewritten to the present tense, is what gets read. An unhidden decorative SVG inside
   * a button is a common way to end up with a control announced as "Sign in graphic".
   *
   * Control: `aria-hidden="true"` on the svg in `Spinner`.
   * Deleted → red, "expect(element).toHaveAttribute(\"aria-hidden\", \"true\") … Expected
   * the element to have attribute: aria-hidden=\"true\" / Received: null".
   */
  it("is decorative", () => {
    const { container } = render(<Spinner />);

    expect(container.querySelector("svg")).toHaveAttribute("aria-hidden", "true");
  });
});
