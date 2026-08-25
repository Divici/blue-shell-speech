import { expect, type Locator, type Page } from "@playwright/test";

/**
 * Reaching the site navigation at whatever width the test is running.
 *
 * `SiteHeader` changes navigation shape at Tailwind's `md` breakpoint: at or above it the
 * links sit inline in the bar, below it they collapse behind a disclosure button. A test
 * that only knows the inline shape reports "About is missing" on a phone — which is a fact
 * about the test, not about the page.
 *
 * This deliberately does NOT accept either shape at either width. Each branch pins the one
 * that must be there and asserts the other is not offered. A helper tolerant of both would
 * repeat the mistake that let the dead-anchor defect ship (DECISIONS.md D042 finding #1):
 * a check written to accept two shapes cannot tell you which one you have.
 */

/** Tailwind's `md`. Below it the header renders the hamburger; at or above it, the list. */
export const NAV_COLLAPSE_WIDTH = 768;

/**
 * Returns the `Main` navigation landmark with its links reachable.
 *
 * The same locator at every width, because the disclosure panel lives INSIDE the landmark —
 * anything else would mean a phone user's navigation is not in the page's navigation.
 */
export async function revealMainNav(page: Page): Promise<Locator> {
  const nav = page.getByRole("navigation", { name: "Main" });
  // By attribute, not by role: at desktop widths the button is display:none and therefore
  // absent from the accessibility tree, and a role query would match nothing at all —
  // making "the hamburger is not offered" true by construction rather than by fact.
  const toggle = nav.locator("button[aria-controls]");
  const width = page.viewportSize()?.width ?? NAV_COLLAPSE_WIDTH;

  if (width >= NAV_COLLAPSE_WIDTH) {
    await expect(toggle, "the disclosure button must still exist in the markup").toBeAttached();
    await expect(toggle, "it must not be offered once the links fit inline").toBeHidden();
    return nav;
  }

  await expect(toggle, "below md the links must collapse behind a disclosure").toBeVisible();
  await expect(
    nav.getByRole("button", { name: /menu/i }),
    "the icon-only button needs an accessible name",
  ).toBeVisible();
  await expect(toggle, "a closed disclosure reports itself closed").toHaveAttribute(
    "aria-expanded",
    "false",
  );

  const panelId = await toggle.getAttribute("aria-controls");
  await toggle.click();

  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  await expect(
    page.locator(`#${panelId}`),
    "aria-controls must name the panel that actually opens",
  ).toBeVisible();

  return nav;
}
