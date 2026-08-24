import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

/**
 * The authentication boundary.
 *
 * These assert what must be true with no valid session, which is the state an attacker is
 * always in. They deliberately do not need the API running: every assertion here is about
 * the BFF refusing to render, and that refusal must not depend on a downstream service
 * being reachable.
 */

test.describe("unauthenticated access", () => {
  test("the dashboard redirects to sign-in and renders nothing", async ({ page }) => {
    const response = await page.goto("/dashboard");

    await expect(page).toHaveURL(/\/login$/);

    // Not merely redirected — the protected content must never have been sent.
    const body = (await page.locator("body").textContent()) ?? "";
    expect(body).not.toContain("Welcome back");
    expect(response?.status()).toBeLessThan(400);
  });

  /**
   * Step two must not be reachable without completing step one. Otherwise it becomes a
   * place to brute-force six digits, and an oracle for whether an account exists.
   */
  test("the MFA step is unreachable without a pending sign-in", async ({ page }) => {
    await page.goto("/login/verify");
    await expect(page).toHaveURL(/\/login$/);
  });

  test("MFA enrolment is unreachable without a pending sign-in", async ({ page }) => {
    await page.goto("/login/enrol");
    await expect(page).toHaveURL(/\/login$/);
  });

  test("sign-in renders and asks for email and password", async ({ page }) => {
    await page.goto("/login");

    await expect(page.getByRole("heading", { name: /provider sign-in/i })).toBeVisible();
    await expect(page.getByLabel("Email")).toBeVisible();
    await expect(page.getByLabel("Password")).toBeVisible();
  });

  /**
   * Authenticated responses must never be cached. A cached redirect — or worse, a cached
   * dashboard — served to the next visitor is a session leak. This is ranked the most
   * likely accidental disclosure in docs/THREAT_MODEL.md.
   */
  test("authenticated routes are never cached", async ({ page }) => {
    for (const route of ["/dashboard", "/login", "/login/verify"]) {
      const response = await page.goto(route);
      const cacheControl = response?.headers()["cache-control"] ?? "";

      expect(cacheControl, `${route} must not be cacheable`).toMatch(
        /no-store|no-cache|private|max-age=0/,
      );
    }
  });

  test("sign-in sets no cookie before credentials are supplied", async ({ page, context }) => {
    await page.goto("/login");

    const cookies = await context.cookies();
    expect(cookies.filter((c) => c.name.startsWith("bss_"))).toHaveLength(0);
  });

  test("rejects an empty submission without contacting the API", async ({ page }) => {
    await page.goto("/login");

    // Bypass the browser's own required-field check to reach the server action, which is
    // the control that actually matters.
    await page.locator("#email").evaluate((el) => el.removeAttribute("required"));
    await page.locator("#password").evaluate((el) => el.removeAttribute("required"));

    await page.getByRole("button", { name: /sign in/i }).click();

    // Scoped to the form: Next renders its own role="alert" route announcer, so an
    // unscoped locator matches two elements and fails on strict mode.
    await expect(page.locator("form").getByRole("alert")).toContainText(/email and password/i);
  });

  test("sign-in has no accessibility violations", async ({ page }) => {
    await page.goto("/login");

    const results = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
      .analyze();

    expect(
      results.violations,
      results.violations.map((v) => `${v.id}: ${v.description}`).join("\n"),
    ).toEqual([]);
  });

  test("sign-in is excluded from search indexing", async ({ page }) => {
    await page.goto("/login");

    const robots = await page.locator('meta[name="robots"]').getAttribute("content");
    expect(robots).toContain("noindex");
  });
});

/**
 * The authenticated route group.
 *
 * Every route under app/(app) inherits the layout's session check and force-dynamic. These
 * assert that inheritance actually holds — a new page added to the group must be
 * protected by existing there, not by remembering to add a guard.
 */
test.describe("patient routes are protected", () => {
  const routes = ["/dashboard", "/patients", "/patients/new"];

  for (const route of routes) {
    test(`${route} redirects to sign-in without a session`, async ({ page }) => {
      await page.goto(route);
      await expect(page).toHaveURL(/\/login$/);
    });
  }

  test("a patient record is unreachable without a session", async ({ page }) => {
    // A real-looking identifier must behave exactly like any other: redirect, reveal
    // nothing about whether it exists.
    await page.goto("/patients/11111111-1111-1111-1111-111111111111");
    await expect(page).toHaveURL(/\/login$/);
  });

  test("no PHI-bearing route is cacheable", async ({ page }) => {
    for (const route of routes) {
      const response = await page.goto(route);
      expect(response?.headers()["cache-control"] ?? "", route).toMatch(
        /no-store|no-cache|private|max-age=0/,
      );
    }
  });
});
