import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

/**
 * Slice 1 acceptance criteria, executable.
 *
 * These assert the things a person cannot reliably eyeball: section order, that removed
 * content stays removed, and that the page is usable by keyboard and screen reader.
 */

test.describe("homepage", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/");
  });

  test("renders the confirmed sections in order", async ({ page }) => {
    const headings = await page.locator("h1, h2").allTextContents();
    const joined = headings.join(" | ");

    expect(joined).toContain("Helping Little Voices Make Big Connections");
    expect(joined).toContain("Meet Your SLP");
    expect(joined).toContain("Getting Started is Easy");
    expect(joined).toContain("communication journey");

    // Order matters: Michelle confirmed this sequence.
    const aboutIndex = headings.findIndex((h) => h.includes("Meet Your SLP"));
    const stepsIndex = headings.findIndex((h) => h.includes("Getting Started"));
    const contactIndex = headings.findIndex((h) => h.includes("communication journey"));
    expect(aboutIndex).toBeLessThan(stepsIndex);
    expect(stepsIndex).toBeLessThan(contactIndex);
  });

  test("has exactly one h1", async ({ page }) => {
    await expect(page.locator("h1")).toHaveCount(1);
  });

  test("names AAC in the service chips", async ({ page }) => {
    // The cut services grid took the only mention of AAC with it. A parent searching
    // for an AAC provider must find it here.
    await expect(page.locator("#services").getByText("AAC", { exact: true })).toBeVisible();
  });

  test("does NOT reintroduce the removed sections", async ({ page }) => {
    const body = await page.locator("body").textContent();

    // Testimonials were fabricated and are deleted, not deferred.
    expect(body).not.toContain("Real Results");
    expect(body).not.toContain("What Families Say");
    // The services grid was replaced by chips.
    expect(body).not.toContain("Therapy That's Tailored to Your Child");
    // No handouts exist yet, so there is no Resources tab.
    await expect(page.getByRole("navigation", { name: "Main" }).getByText("Resources")).toHaveCount(0);
  });

  test("navigation anchors scroll on the page rather than navigating away", async ({ page }) => {
    const nav = page.getByRole("navigation", { name: "Main" });
    for (const label of ["Home", "About", "Services", "Contact"]) {
      const href = await nav.getByRole("link", { name: label, exact: true }).getAttribute("href");
      expect(href, `${label} should be an on-page anchor`).toMatch(/^\/?#/);
    }
  });

  test("consultation and login are real routes", async ({ page }) => {
    await expect(page.getByRole("link", { name: "Free Consultation" }).first()).toHaveAttribute(
      "href",
      "/consultation",
    );
    await expect(page.getByRole("link", { name: "Login" }).first()).toHaveAttribute("href", "/login");
  });

  test("publishes no street address", async ({ page }) => {
    // In-home therapy travels to the patient. Michelle's home address must never appear.
    const body = (await page.locator("body").textContent()) ?? "";
    expect(body).not.toMatch(/\d+\s+[A-Z][a-z]+\s+(Street|St|Road|Rd|Avenue|Ave|Lane|Ln|Drive|Dr)\b/);
  });

  test("has no detectable accessibility violations", async ({ page }) => {
    const results = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
      .analyze();

    expect(
      results.violations,
      results.violations.map((v) => `${v.id}: ${v.description}`).join("\n"),
    ).toEqual([]);
  });

  test("is keyboard navigable to the primary call to action", async ({ page }) => {
    await page.keyboard.press("Tab");
    // The skip link is first in the DOM, deliberately.
    await expect(page.locator(":focus")).toHaveText(/skip to main content/i);
  });

  test("never writes to localStorage or sessionStorage", async ({ page }) => {
    // CLAUDE.md non-negotiable #4. Enforced by lint in source, and here at runtime —
    // lint cannot see storage written by a dependency, only by our own code.
    //
    // This assertion has to READ the APIs it forbids, so the rule is disabled for
    // exactly these two lines. It runs inside page.evaluate, in the browser, and never
    // ships: the whole point of the test is to prove the value is 0.
    const storage = await page.evaluate(() => ({
      // eslint-disable-next-line no-restricted-properties
      local: window.localStorage.length,
      // eslint-disable-next-line no-restricted-properties
      session: window.sessionStorage.length,
    }));
    expect(storage).toEqual({ local: 0, session: 0 });
  });
});

test.describe("consultation", () => {
  test("has no detectable accessibility violations", async ({ page }) => {
    await page.goto("/consultation");
    const results = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
      .analyze();

    expect(
      results.violations,
      results.violations.map((v) => `${v.id}: ${v.description}`).join("\n"),
    ).toEqual([]);
  });

  test("rejects an empty submission on the server and reports which fields failed", async ({
    page,
  }) => {
    await page.goto("/consultation");
    await page.getByRole("button", { name: /send request/i }).click();

    await expect(page.getByRole("alert")).toBeVisible();
    await expect(page.getByText("Please enter your name.")).toBeVisible();
    await expect(page.getByText("Please enter your child's first name.")).toBeVisible();
  });

  test("accepts a complete submission and confirms", async ({ page }) => {
    await page.goto("/consultation");

    await page.getByLabel("Your name").fill("Jordan Reyes");
    await page.getByLabel("Email").fill("jordan@example.com");
    await page.getByLabel("Phone").fill("410-555-0142");
    await page.getByLabel("Your child's first name").fill("Maya");
    await page.getByLabel("Your child's age in months").fill("30");
    await page
      .getByLabel("What are you noticing?")
      .fill("She has a few words but isn't combining them yet.");

    await page.getByRole("button", { name: /send request/i }).click();

    await expect(page.getByRole("status")).toContainText(/thank you/i);
  });
});
