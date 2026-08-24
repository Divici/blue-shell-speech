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

  /**
   * Anchors must be ROOT-relative, not page-relative.
   *
   * The previous version of this test allowed /^\/?#/, which accepts both "#about" and
   * "/#about" — and ran only on the homepage, where the two behave identically. It passed
   * for the entire period during which the header's links were dead on every other route.
   * A regex tolerant of both shapes cannot tell the correct one from the broken one.
   */
  test("navigation anchors are root-relative", async ({ page }) => {
    const nav = page.getByRole("navigation", { name: "Main" });
    for (const label of ["Home", "About", "Services", "Contact"]) {
      const href = await nav.getByRole("link", { name: label, exact: true }).getAttribute("href");
      expect(href, `${label} must be root-relative so it works off the homepage`).toMatch(
        /^\/#/,
      );
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

  /**
   * The skip link must be the first focusable thing on the page.
   *
   * Asserted structurally rather than by pressing Tab, because WebKit does not focus
   * links on Tab by default — Safari gates that behind "Full Keyboard Access". A
   * Tab-based assertion passes in Chromium and fails in Safari while the page itself is
   * identical and correct in both.
   *
   * What actually matters is unchanged across browsers: the skip link is first in DOM
   * order, it can take focus, it becomes visible when focused, and it targets the main
   * landmark.
   */
  test("the skip link is the first focusable element and reveals itself on focus", async ({
    page,
  }) => {
    const skipLink = page.locator("a.skip-link");

    await expect(skipLink).toHaveAttribute("href", "#main");

    const isFirstFocusable = await page.evaluate(() => {
      const focusable = document.querySelectorAll<HTMLElement>(
        'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])',
      );
      return focusable[0]?.classList.contains("skip-link") ?? false;
    });
    expect(isFirstFocusable, "skip link must come first in DOM order").toBe(true);

    // Hidden off-screen until focused, then pulled into view.
    //
    // Polled rather than read once: the reveal is a 150 ms CSS transition, so reading
    // the position immediately after .focus() returns the pre-transition value and the
    // assertion fails on a page that works correctly.
    const before = await skipLink.evaluate((el) => el.getBoundingClientRect().top);
    await skipLink.focus();
    await expect
      .poll(
        async () => skipLink.evaluate((el) => el.getBoundingClientRect().top),
        { message: "skip link must become visible on focus" },
      )
      .toBeGreaterThan(before);

    await expect(page.locator("#main")).toBeAttached();
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

/**
 * Cross-route navigation.
 *
 * These run on a page that is NOT the homepage, which is the only place the dead-anchor
 * defect was observable. Asserting nav behaviour solely on the homepage is what let it
 * ship.
 */
test.describe("navigation from a non-home route", () => {
  for (const route of ["/consultation", "/login"]) {
    test(`header nav works from ${route}`, async ({ page }) => {
      await page.goto(route);

      const nav = page.getByRole("navigation", { name: "Main" });
      const about = nav.getByRole("link", { name: "About", exact: true });

      await expect(about).toHaveAttribute("href", "/#about");

      // Actually follow it: the target section must exist where we land.
      await about.click();
      await page.waitForURL(/\/#about$/);
      await expect(page.locator("#about")).toBeVisible();
    });
  }
});

/**
 * Regression: a validation failure must not erase what the parent typed.
 *
 * The concerns field matters most — it is the longest thing on the form and the hardest
 * to retype, and it is a parent describing their child's difficulties.
 */
test("consultation preserves input when validation fails", async ({ page }) => {
  await page.goto("/consultation");

  const concerns =
    "She has around ten words but is not combining them, and she gets frustrated when we do not understand.";

  await page.getByLabel("Your name").fill("Jordan Reyes");
  await page.getByLabel("Your child's first name").fill("Maya");
  await page.getByLabel("Your child's age in months").fill("30");
  await page.getByLabel("What are you noticing?").fill(concerns);
  // Deliberately invalid — everything else must survive.
  await page.getByLabel("Email").fill("not-an-email");

  await page.getByRole("button", { name: /send request/i }).click();

  await expect(page.getByRole("alert")).toBeVisible();
  await expect(page.getByLabel("What are you noticing?")).toHaveValue(concerns);
  await expect(page.getByLabel("Your name")).toHaveValue("Jordan Reyes");
  await expect(page.getByLabel("Your child's first name")).toHaveValue("Maya");
  await expect(page.getByLabel("Your child's age in months")).toHaveValue("30");
});

/**
 * Security headers.
 *
 * docs/SECURITY.md lists these as implemented controls. Asserting them here means the
 * document cannot drift into describing a policy that is not actually served — which is
 * precisely what happened with CSP, absent for the whole of slice 1 while SECURITY.md
 * presented it as shipped.
 */
test("serves the documented security headers", async ({ page }) => {
  const response = await page.goto("/");
  const headers = response?.headers() ?? {};

  expect(headers["content-security-policy"], "CSP must be present").toBeTruthy();
  expect(headers["content-security-policy"]).toContain("frame-ancestors 'none'");
  expect(headers["content-security-policy"]).toContain("object-src 'none'");
  expect(headers["x-content-type-options"]).toBe("nosniff");
  expect(headers["referrer-policy"]).toBe("strict-origin-when-cross-origin");
  expect(headers["x-frame-options"]).toBe("DENY");
  expect(headers["strict-transport-security"]).toContain("max-age=63072000");
  expect(headers["permissions-policy"]).toContain("microphone=(self)");

  // Next.js advertises itself by default; there is no reason to tell an attacker the
  // framework and version.
  expect(headers["x-powered-by"]).toBeUndefined();
});
