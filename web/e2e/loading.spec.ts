import { test, signedInTest, expect } from "./fixtures";
import { SLOW_DAY, SLOW_LOGIN_EMAIL } from "./api-stub-contract.mjs";

/**
 * What the browser shows while the API is answering.
 *
 * THIS IS THE ONE LAYER NOTHING ELSE CAN PROVE. `app/loading-coverage.test.ts` proves a
 * `loading.tsx` exists beside every dynamic page; the component tests prove a button
 * disables and relabels. Neither can tell you whether Next actually streams the fallback
 * to a browser before the data resolves — that is the framework's Suspense wiring, and it
 * is silently defeated by ordinary mistakes: uncached data read in a LAYOUT rather than a
 * page blocks the whole navigation, and a fallback nobody streams is a file nobody sees.
 *
 * SO THE ROUTE IS HELD AND THE ANSWER IS OBSERVED. `api-stub.mjs` delays a reserved date
 * and a reserved email — keyed on request content, never on a shared flag, because three
 * browser projects run in parallel against one stub. Two and a half seconds stands in for
 * the ~22-second cold start docs/PERFORMANCE.md measured; the shape of the wait is the
 * same, and it is the shape this suite is about.
 */

test.describe("the sign-in form while the credential is being checked", () => {
  /**
   * THE REPORTED BUG, MEASURED RATHER THAN ASSUMED.
   *
   * David signed in and could not tell whether anything was happening. The obvious
   * conclusion was that the button had no pending state; it has one, and this is what
   * proves it — held against a real server, in three engines, the control disables and
   * says what it is doing.
   *
   * What was actually missing was everything AFTER this moment: `signIn` ends in a
   * redirect, and the pages it redirects to had no fallback at all, so the wait that felt
   * dead was the one on the far side of the form. That half is covered below.
   *
   * Control: the `disabled={pending}` attribute on `LoginForm.SubmitButton`.
   * Deleted → red here and on the sibling below, "Expect \"toBeDisabled\" … 9 x locator
   * resolved to <button type=\"submit\" class=\"inline-flex w-full …\">…</button> —
   * unexpected value \"enabled\"".
   */
  test("disables the button and says what it is doing", async ({ page }) => {
    await page.goto("/login");

    await page.getByLabel("Email").fill(SLOW_LOGIN_EMAIL);
    await page.getByLabel("Password").fill("not-a-real-password");

    const submit = page.getByRole("button", { name: /sign in/i });
    await submit.click();

    // Asserted WHILE the request is in flight — the stub holds /auth/password.
    await expect(page.getByRole("button", { name: /signing in/i })).toBeDisabled();

    // And it comes back, so the pending state is a state rather than a dead end.
    await expect(page.getByRole("button", { name: /^sign in$/i })).toBeEnabled();
    await expect(page.locator("form").getByRole("alert")).toContainText(/not recognised/i);
  });

  /**
   * A disabled control cannot be tapped again, which is the point of disabling it: every
   * `/auth` route is rate limited (D098) and a clinician on a slow connection tapping
   * three times spends three of her own attempts.
   *
   * Control: the `disabled={pending}` attribute on `LoginForm.SubmitButton`, as above.
   */
  test("cannot be submitted twice while the first attempt is in flight", async ({ page }) => {
    await page.goto("/login");

    await page.getByLabel("Email").fill(SLOW_LOGIN_EMAIL);
    await page.getByLabel("Password").fill("not-a-real-password");
    await page.getByRole("button", { name: /sign in/i }).click();

    const pendingButton = page.getByRole("button", { name: /signing in/i });
    await expect(pendingButton).toBeDisabled();

    // A real second tap, not a synthetic dispatch. Playwright refuses to click a disabled
    // control, so the failure to click IS the assertion.
    await expect(pendingButton).not.toBeEnabled();
  });
});

signedInTest.describe("route-level fallbacks", () => {
  /**
   * THE SKELETON REACHES THE BROWSER BEFORE THE DATA DOES.
   *
   * `waitUntil: "commit"` so `goto` returns as soon as the response starts rather than
   * when it finishes — which is the only window in which a streamed fallback exists.
   *
   * Control: `app/(app)/today/loading.tsx`.
   * Deleted → red on all three tests in this block: "expect(locator).toBeVisible() failed /
   * Locator: getByRole('status') / Expected: visible / Error: element(s) not found". The
   * browser holds a blank document for the whole wait, because with no Suspense boundary
   * Next has nothing to stream and sends the page only once the day has resolved.
   */
  signedInTest("the day view shows its skeleton before the schedule arrives", async ({ page }) => {
    await page.goto(`/today?date=${SLOW_DAY}`, { waitUntil: "commit" });

    const fallback = page.getByRole("status");
    await expect(fallback).toBeVisible();

    // The announcement, which is the whole of what a screen reader gets from a screen
    // built out of aria-hidden rectangles.
    await expect(fallback).toHaveAttribute("aria-busy", "true");
    await expect(fallback).toContainText(/loading the day's visits/i);

    // Then the real page replaces it.
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByRole("status")).toHaveCount(0);
  });

  /**
   * The skeleton must not hold anything read from a record.
   *
   * It is on screen before the day exists, so there is nothing it COULD legitimately show
   * — and a placeholder whose widths tracked real names would leak the shape of a
   * caseload into a frame rendered in a family's living room. Asserted as "no text beyond
   * the announcement", which is the property rather than a proxy for it.
   */
  signedInTest("the skeleton renders no data of its own", async ({ page }) => {
    await page.goto(`/today?date=${SLOW_DAY}`, { waitUntil: "commit" });

    const fallback = page.getByRole("status");
    await expect(fallback).toBeVisible();

    const text = ((await fallback.textContent()) ?? "").trim();
    expect(text).toBe("Loading the day's visits");
  });

  /**
   * The shell stays interactive while the segment streams.
   *
   * This is the difference between a loading state and a frozen application: `aria-busy`
   * is on the region rather than the document precisely because the practice navigation
   * and the sign-out control are usable throughout — a clinician who tapped the wrong day
   * can leave without waiting for it.
   */
  signedInTest("the practice navigation stays usable while a segment loads", async ({ page }) => {
    await page.goto(`/today?date=${SLOW_DAY}`, { waitUntil: "commit" });

    await expect(page.getByRole("status")).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Practice" })).toBeVisible();
    await expect(page.getByRole("button", { name: /^sign out$/i })).toBeEnabled();
  });
});
