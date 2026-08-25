import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync, existsSync } from "node:fs";
import path from "node:path";

/**
 * Every dynamically-rendered page has a loading fallback.
 *
 * WHY THIS IS A TEST AND NOT A CONVENTION. `api` scales to zero and its database
 * auto-pauses, so the first request of a day is measured in tens of seconds
 * (docs/PERFORMANCE.md, ~22 s cold start; `lib/api/timeouts.ts` is prepared to wait
 * twelve and a half minutes). A dynamic page with no `loading.tsx` sends NOTHING until
 * that round trip finishes: the browser sits on the previous screen with no cursor
 * change, no title change and no paint. Every screen in this product was built against a
 * local database answering in single digits of milliseconds, which is why nobody saw it.
 *
 * THE SET IS DERIVED, NOT LISTED (docs/TEST_STRATEGY.md, "a guard over a SET enumerates
 * the set"). The predicate is a fact about the tree: a page is dynamic when it, or a
 * layout above it, declares `export const dynamic = "force-dynamic"`. A statically
 * prerendered page — the homepage, the consultation form — has nothing to wait for and is
 * correctly exempt. Add a page to `app/(app)` and it inherits that layout's directive, so
 * it arrives inside this guard rather than being remembered into it.
 *
 * WHAT IT DOES NOT PROVE: that the fallback actually reaches a browser. That is Next's
 * Suspense wiring rather than this repository's, and it is measured against a real server
 * holding a real response in `e2e/loading.spec.ts`.
 */

const APP = path.resolve(__dirname);

interface RouteSegment {
  /** Directory holding the page.tsx, relative to app/. */
  directory: string;
  /** The URL path, with route groups removed and dynamic segments left as written. */
  route: string;
  dynamic: boolean;
  hasLoading: boolean;
}

const FORCE_DYNAMIC = /export\s+const\s+dynamic\s*=\s*["']force-dynamic["']/;

function declaresForceDynamic(file: string): boolean {
  return existsSync(file) && FORCE_DYNAMIC.test(readFileSync(file, "utf8"));
}

/**
 * Walks `app/`, carrying down whether an ancestor layout has already forced dynamic
 * rendering — which is how `app/(app)/layout.tsx` covers all nine screens behind the
 * login without any of them repeating the directive.
 */
function walk(directory: string, segments: string[], inheritedDynamic: boolean): RouteSegment[] {
  const dynamicHere =
    inheritedDynamic || declaresForceDynamic(path.join(directory, "layout.tsx"));

  const found: RouteSegment[] = [];

  const page = path.join(directory, "page.tsx");
  if (existsSync(page)) {
    found.push({
      directory: path.relative(APP, directory) || ".",
      route: "/" + segments.join("/"),
      dynamic: dynamicHere || declaresForceDynamic(page),
      hasLoading: existsSync(path.join(directory, "loading.tsx")),
    });
  }

  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;

    // Route groups are parentheses in the filesystem and nothing in the URL.
    const next = /^\(.*\)$/.test(entry.name) ? segments : [...segments, entry.name];
    found.push(...walk(path.join(directory, entry.name), next, dynamicHere));
  }

  return found;
}

const routes = walk(APP, [], false);
const dynamicRoutes = routes.filter((r) => r.dynamic);

describe("loading fallbacks", () => {
  /**
   * The walk found something.
   *
   * A walk that returns nothing asserts nothing, and the file below stays green — so a
   * rename of `app/` would silently delete this guard rather than break it
   * (docs/TEST_STRATEGY.md). A FLOOR, never an exact count: an exact count is the
   * hard-coded list arriving by another door.
   *
   * Control: the `walk(APP, [], false)` call — the traversal reaching the whole app tree.
   * Narrowed to `walk(path.join(APP, "login"), [], false)` → red, "Dynamic routes found:
   * /, /enrol, /verify … expected 3 to be greater than 8", and the assertion below then
   * silently covers three routes instead of twelve.
   */
  it("discovers the dynamic routes rather than listing them", () => {
    expect(
      dynamicRoutes.length,
      `Dynamic routes found: ${dynamicRoutes.map((r) => r.route).join(", ")}`,
    ).toBeGreaterThan(8);
  });

  /**
   * Control: `app/(app)/today/loading.tsx`.
   * Deleted → red, "Dynamic pages with no loading.tsx: /today (app/(app)/today) …
   * expected [ '/today' ] to deeply equal []".
   */
  it("gives every dynamically-rendered page one", () => {
    const missing = dynamicRoutes.filter((r) => !r.hasLoading);

    expect(
      missing.map((r) => r.route),
      `Dynamic pages with no loading.tsx: ${missing
        .map((r) => `${r.route} (app/${r.directory})`)
        .join(", ")}`,
    ).toEqual([]);
  });

  /**
   * The exemption is stated rather than assumed.
   *
   * A statically prerendered page has no server round trip to wait on, and a skeleton
   * that flashes before content the CDN already holds is a regression in perceived speed,
   * not an improvement. If one of these ever becomes dynamic, the assertion above starts
   * demanding a fallback for it — which is the behaviour that makes the derived set worth
   * having.
   *
   * Control: `export const dynamic = "force-dynamic"` in `app/(app)/layout.tsx` — the
   * directive nine screens inherit rather than repeat. Deleted → red, "expected [ '/',
   * '/appointments/new', …(9) ] to deeply equal [ '/', '/consultation' ]", and the guard
   * above went red with it: "Dynamic routes found: /login, /login/enrol, /login/verify:
   * expected 3 to be greater than 8". Both halves of the predicate are load-bearing.
   */
  it("leaves statically prerendered pages alone", () => {
    const staticRoutes = routes.filter((r) => !r.dynamic);

    expect(staticRoutes.map((r) => r.route).sort()).toEqual(["/", "/consultation"]);
  });
});
