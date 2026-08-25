import { describe, it, expect } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

/**
 * The header's disclosure navigation, as a state machine.
 *
 * Below Tailwind's `md` the inline list is display:none and this panel IS the navigation.
 * jsdom applies no stylesheet, so both shapes are in the document here and every query is
 * scoped — this file is about the open/closed behaviour and where the panel sits, not
 * about which shape a viewport shows. The viewport half is asserted in
 * e2e/homepage.spec.ts, in a real engine at a real width.
 */

import { SiteHeader } from "./SiteHeader";

function toggle() {
  return screen.getByRole("button", { name: /menu/i });
}

function panel() {
  return document.getElementById("mobile-menu");
}

/**
 * Stated here rather than imported from lib/site-content: a test that reads the same
 * constant the component renders agrees with whatever that constant says, wrong included.
 */
const EXPECTED_LINKS = [
  { label: "Home", href: "/#top" },
  { label: "About", href: "/#about" },
  { label: "Services", href: "/#services" },
  { label: "Contact", href: "/#contact" },
  { label: "Free Consultation", href: "/consultation" },
  { label: "Login", href: "/login" },
];

describe("SiteHeader disclosure navigation", () => {
  it("starts closed, and says so", () => {
    render(<SiteHeader />);

    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(toggle()).toHaveAccessibleName("Open menu");
    expect(panel()).toBeNull();
  });

  it("opens on click and renames itself", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);

    await user.click(toggle());

    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(toggle()).toHaveAccessibleName("Close menu");
    expect(panel()).not.toBeNull();
  });

  /**
   * `aria-controls` naming an element that is not in the document is a promise the header
   * cannot keep. The id is asserted against the panel that actually appears rather than
   * against a literal, so renaming one and not the other fails here.
   */
  it("controls the panel it names", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);
    await user.click(toggle());

    expect(panel()?.id).toBe(toggle().getAttribute("aria-controls"));
  });

  it("carries the same root-relative links the wide layout carries", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);
    await user.click(toggle());

    const menu = within(panel()!);
    for (const item of EXPECTED_LINKS) {
      expect(menu.getByRole("link", { name: item.label })).toHaveAttribute("href", item.href);
    }
  });

  /**
   * On a phone this panel is the only navigation there is, so it belongs in the navigation
   * landmark. It used to render after </nav>, which left landmark-first assistive
   * technology with a logo and a button and no way through the site.
   */
  it("renders the panel inside the Main navigation landmark", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);
    await user.click(toggle());

    const nav = screen.getByRole("navigation", { name: "Main" });
    expect(nav).toContainElement(panel());
    // After its button, so the tab order walks from one into the other with no trap.
    expect(
      toggle().compareDocumentPosition(panel()!) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  /**
   * Escape is pressed from INSIDE the panel, which is the only shape of this test that
   * can fail. Pressing it straight after the click leaves focus on the button either way —
   * the click put it there — so the assertion would hold with the focus return deleted.
   */
  it("closes on Escape and hands focus back to the button", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);

    await user.click(toggle());
    within(panel()!).getByRole("link", { name: "About" }).focus();

    await user.keyboard("{Escape}");

    expect(panel()).toBeNull();
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    // Otherwise focus is stranded on an element that no longer exists and the next Tab
    // restarts from the top of the document.
    expect(toggle()).toHaveFocus();
  });

  /**
   * The effect cleans up its document-level keydown listener. Asserting that it is gone
   * needs an observable consequence, because a leaked listener still calls
   * setMenuOpen(false) on an already-closed menu and nothing moves.
   *
   * The consequence is the focus() call beside it: a stale handler makes Escape ANYWHERE
   * on the page yank focus into the header, from a form field, from the middle of a
   * paragraph, from wherever the reader was.
   */
  it("stops listening for Escape once closed", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);

    await user.click(toggle());
    await user.keyboard("{Escape}");

    const elsewhere = screen.getByRole("link", { name: "About" });
    elsewhere.focus();
    await user.keyboard("{Escape}");

    expect(elsewhere, "a closed menu must not answer Escape").toHaveFocus();
  });

  it("closes again on a second click", async () => {
    const user = userEvent.setup();
    render(<SiteHeader />);

    await user.click(toggle());
    await user.click(toggle());

    expect(panel()).toBeNull();
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
  });
});
