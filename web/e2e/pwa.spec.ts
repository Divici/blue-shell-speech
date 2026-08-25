import AxeBuilder from "@axe-core/playwright";
import { test, expect } from "./fixtures";

/**
 * The installable shell, in real browsers.
 *
 * `mobile-safari` is the project that counts here: the deployment target is an iPhone with
 * this app on its home screen, used in a car between home visits. Two of the constraints
 * that shape the whole dictation slice are iOS behaviours (docs/ARCHITECTURE.md) —
 * **only a home-screen-installed PWA escapes Safari's 7-day storage eviction**, and
 * installation on iOS is a manual Share-sheet action driven entirely by page metadata. So
 * "the metadata is right" is not a nicety on this project; it is what decides whether an
 * encrypted dictation draft survives a week.
 *
 * The unit suite (`lib/pwa/service-worker.test.ts`) proves the worker's routing decisions
 * against the shipped `public/sw.js`. This file proves the wiring: that a page actually
 * registers it, that the browser accepts the manifest, and that after using the app the
 * Cache API contains nothing but static assets.
 */

interface ManifestIcon {
  src: string;
  sizes?: string;
  type?: string;
  purpose?: string;
}

interface WebManifest {
  name?: string;
  short_name?: string;
  start_url?: string;
  scope?: string;
  display?: string;
  theme_color?: string;
  icons?: ManifestIcon[];
}

test.describe("installability", () => {
  test("every page links a manifest the browser will accept", async ({ page, request }) => {
    await page.goto("/");

    const href = await page.locator('link[rel="manifest"]').getAttribute("href");
    expect(href, "no <link rel=manifest> — nothing is installable without one").toBeTruthy();

    const response = await request.get(href as string);
    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"]).toContain("manifest");

    const manifest = (await response.json()) as WebManifest;

    expect(manifest.name).toBe("Blue Shell Speech");
    expect(manifest.display).toBe("standalone");
    expect(manifest.start_url).toBe("/today");
    expect(manifest.scope).toBe("/");
  });

  /**
   * Every icon the manifest promises is really there — checked against the manifest the
   * server serves, not a list in this file. A 404 here is the commonest reason a browser
   * silently declines to offer an install, and it reports nothing to the page.
   */
  test("every icon the manifest declares is served", async ({ page, request }) => {
    await page.goto("/");
    const href = (await page.locator('link[rel="manifest"]').getAttribute("href")) as string;
    const manifest = (await (await request.get(href)).json()) as WebManifest;

    const icons = manifest.icons ?? [];
    expect(icons.length, `Icons declared: ${icons.map((i) => i.src).join(", ")}`).toBeGreaterThan(2);

    for (const icon of icons) {
      const response = await request.get(icon.src);

      expect(response.status(), `${icon.src} is declared in the manifest`).toBe(200);
      expect(response.headers()["content-type"], icon.src).toContain("image/");
    }
  });

  /**
   * The two things iOS reads that the manifest does not supply.
   *
   * `apple-touch-icon` is the home-screen image — Safari does not use the manifest's
   * `icons` for it — and `apple-mobile-web-app-title` is the label under it, in place of
   * `short_name`.
   *
   * Standalone display is NOT asserted through `apple-mobile-web-app-capable`. That tag is
   * the pre-manifest mechanism and is no longer recommended; Apple's own guidance and
   * WebKit's is that `display: "standalone"` in the manifest is what puts an installed app
   * in its own window, and Next 16 emits the standardised `mobile-web-app-capable` rather
   * than the Apple-prefixed name. Both are asserted: the meta because it is what the
   * framework renders, and the manifest's `display` in the test above because on iOS it is
   * the control that actually decides.
   */
  test("carries the iOS home-screen metadata", async ({ page, request }) => {
    await page.goto("/");

    const icon = await page.locator('link[rel="apple-touch-icon"]').first().getAttribute("href");
    expect(icon, "iOS reads apple-touch-icon, not the manifest icons").toBeTruthy();

    const response = await request.get(icon as string);
    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"]).toContain("image/png");

    await expect(page.locator('meta[name="apple-mobile-web-app-title"]')).toHaveAttribute(
      "content",
      "Blue Shell",
    );
    await expect(page.locator('meta[name="mobile-web-app-capable"]')).toHaveAttribute(
      "content",
      "yes",
    );
  });

  /**
   * The worker script must revalidate. Left cacheable, a browser can keep serving the old
   * one for up to a day, which means a deploy that changes the offline shell does not
   * reach an installed app until tomorrow.
   */
  test("serves the worker script with a revalidating cache header", async ({ request }) => {
    const response = await request.get("/sw.js");

    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"]).toMatch(/javascript/);
    expect(response.headers()["cache-control"]).toMatch(/max-age=0|no-cache|must-revalidate/);
  });
});

test.describe("the service worker", () => {
  /**
   * The page registers it — this test does not.
   *
   * Registering from the test would prove the worker works and leave the wiring untested,
   * which is how a PWA ships with a perfect service worker nothing ever installs.
   *
   * Control: `<ServiceWorkerRegistration />` in `app/layout.tsx`.
   * Deleted → red, "Error: page.evaluate: Test timeout of 30000ms exceeded" on the
   * `navigator.serviceWorker.ready` await — nothing ever registers, so the promise the
   * whole feature hangs off never settles.
   */
  test("is registered by the page and controls the whole origin", async ({ page }) => {
    await page.goto("/");

    const scope = await page.evaluate(async () => {
      const registration = await navigator.serviceWorker.ready;
      return registration.scope;
    });

    expect(scope, "the worker must control the whole origin, not a subtree").toMatch(/\/$/);

    const controlled = await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      // `clients.claim()` takes over pages loaded before the worker activated; without it
      // the first offline navigation of the session bypasses the worker entirely.
      return navigator.serviceWorker.controller !== null;
    });

    expect(controlled, "the worker did not claim the page that registered it").toBe(true);
  });

  /**
   * THE ONE THAT MATTERS, in a real browser.
   *
   * Browse the app, then read every entry in the Cache API and require all of them to be
   * static files. `no-store` and `force-dynamic` stop a PHI page reaching an HTTP cache or
   * a CDN; neither of them says anything about the Cache API, which is script-controlled
   * storage on the device — the same class of exposure as `localStorage`, which CLAUDE.md
   * non-negotiable #4 prohibits outright.
   *
   * The routes below are unauthenticated redirects here, because the E2E suite has no
   * session. That is enough for what this asserts: the worker's decision not to cache is
   * made on the request, before it knows what came back.
   */
  test("caches nothing the application rendered", async ({ page }) => {
    await page.goto("/");
    await page.evaluate(() => navigator.serviceWorker.ready);

    for (const route of ["/today", "/patients", "/dashboard", "/consultation", "/login"]) {
      await page.goto(route);
    }

    const cached = await page.evaluate(async () => {
      const names = await caches.keys();
      const urls: string[] = [];

      for (const name of names) {
        const cache = await caches.open(name);
        for (const request of await cache.keys()) urls.push(new URL(request.url).pathname);
      }

      return { names, urls: urls.sort() };
    });

    expect(cached.names, `Caches on the origin: ${cached.names.join(", ")}`).toHaveLength(1);

    for (const url of cached.urls) {
      expect(
        url,
        `${url} is in the Cache API. Only static files under /public may be cached — ` +
          `anything the application renders can carry PHI. Cached: ${cached.urls.join(", ")}.`,
      ).toMatch(/^\/(offline\.(html|css)|icons\/)/);
    }
  });

  /**
   * The offline fallback, in the one browser whose driver can express "no network".
   *
   * `context.setOffline()` and `route.abort()` both break WebKit navigations *above* the
   * service worker in Playwright's implementation — the navigation fails with "WebKit
   * encountered an internal error" / "Blocked by Web Inspector" and the worker's fetch
   * handler is never reached, so the same test on `webkit` and `mobile-safari` would assert
   * a Playwright behaviour rather than the product's. Verified by probe before writing this
   * comment, not assumed.
   *
   * What that leaves untested on iOS is the browser's plumbing, not the decision: the
   * fallback itself is asserted against the shipped `public/sw.js` in
   * `lib/pwa/service-worker.test.ts`, which runs everywhere.
   */
  test("serves the offline shell when the network is gone", async ({ page, context }) => {
    test.skip(
      test.info().project.name !== "chromium",
      "Playwright's WebKit fails the navigation above the service worker; see the comment.",
    );

    await page.goto("/");
    await page.evaluate(() => navigator.serviceWorker.ready);

    await context.setOffline(true);
    await page.goto("/today");

    await expect(page.getByRole("heading", { level: 1 })).toContainText(/offline/i);
    // Honest, not decorative: it must say what is unavailable.
    await expect(page.locator("body")).toContainText(/schedule/i);

    await context.setOffline(false);
  });
});

test.describe("the offline shell", () => {
  test("has no accessibility violations", async ({ page }) => {
    await page.goto("/offline.html");

    const results = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
      .analyze();

    expect(
      results.violations,
      results.violations.map((v) => `${v.id}: ${v.description}`).join("\n"),
    ).toEqual([]);
  });

  test("offers a way back into the app", async ({ page }) => {
    await page.goto("/offline.html");

    await expect(page.getByRole("link", { name: /try again/i })).toBeVisible();
  });
});
