import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { readdirSync } from "node:fs";
import path from "node:path";

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
 * Every page in the authenticated route group, DISCOVERED RATHER THAN LISTED.
 *
 * `app/(app)` is a route group: the parentheses do not appear in the URL, and every page
 * inside inherits the layout's session check and `force-dynamic`. So the routes are a fact
 * about the directory, and reading the directory is the only version of this that stays
 * true. A dynamic segment becomes a synthetic identifier — a real-looking id must behave
 * exactly like any other and reveal nothing about whether the record exists.
 *
 * IT WAS A HARD-CODED LIST OF SIX, under a comment claiming "a new page added to the group
 * must be protected by existing there, not by remembering to add a guard" — while the test
 * itself was a list somebody had to remember to extend. Three of the nine pages were
 * covered by separate named tests, which is how the list stayed plausible. Same defect as
 * the BFF timeout guard in `lib/api/timeouts.test.ts`, one tree over.
 */
function routesInTheAuthenticatedGroup(): string[] {
  const group = path.resolve(__dirname, "..", "app", "(app)");

  function walk(directory: string, segments: string[]): string[] {
    const found: string[] = [];

    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        // Route groups are parentheses in the filesystem and nothing in the URL.
        const segment = /^\(.*\)$/.test(entry.name)
          ? segments
          : [...segments, entry.name];

        found.push(...walk(path.join(directory, entry.name), segment));
        continue;
      }

      if (entry.name === "page.tsx") {
        found.push(
          "/" +
            segments
              .map((s) =>
                /^\[.*\]$/.test(s) ? "11111111-1111-1111-1111-111111111111" : s,
              )
              .join("/"),
        );
      }
    }

    return found;
  }

  return walk(group, []).sort();
}

test.describe("patient routes are protected", () => {
  const routes = routesInTheAuthenticatedGroup();

  /**
   * The walk found the group, and found pages in it.
   *
   * A `for` loop over an empty array registers no tests and this file stays green, so a
   * rename of `app/(app)` would delete this entire describe block silently — which is the
   * failure mode being fixed, arriving by a different door. The floor is loose on purpose:
   * an exact count would put the list back.
   *
   * Control: the `group` path — the walk reaching the whole route group.
   * Narrowed to `app/(app)/patients` → red, "Routes found: /,
   * /11111111-1111-1111-1111-111111111111, /new … expected 3 to be greater than 5", and
   * the loop below silently registers three wrong routes instead of nine, which is what
   * this assertion exists to make loud.
   */
  test("the authenticated group is discovered rather than listed", () => {
    expect(routes.length, `Routes found: ${routes.join(", ")}`).toBeGreaterThan(5);
  });

  /**
   * Control: the `if (!session) redirect("/login")` in `app/(app)/layout.tsx`.
   * Replaced with `if (!session) return <>{children}</>` — rendering the protected page to
   * a visitor with no session, which is the defect — → red on all nine, "expect(page)
   * .toHaveURL(expected) failed", along with the standalone dashboard test above.
   *
   * Re-run because the test set changed shape (D077): it used to be six listed routes plus
   * three named tests for the note, the enquiry and the patient record. All nine now come
   * out of the walk, and all nine went red on the one deletion.
   */
  for (const route of routes) {
    test(`${route} redirects to sign-in without a session`, async ({ page }) => {
      // A clinical note and a patient record are the most sensitive pages here —
      // observations about a child, and a parent's account of their difficulties — and
      // they are covered by this loop rather than by tests of their own, because being
      // inside the group IS the protection under test.
      await page.goto(route);
      await expect(page).toHaveURL(/\/login$/);
    });
  }

  test("no PHI-bearing route is cacheable", async ({ page }) => {
    for (const route of routes) {
      const response = await page.goto(route);
      expect(response?.headers()["cache-control"] ?? "", route).toMatch(
        /no-store|no-cache|private|max-age=0/,
      );
    }
  });
});
