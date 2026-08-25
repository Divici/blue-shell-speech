import { describe, it, expect } from "vitest";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";

/**
 * The service worker, tested against the bytes that ship.
 *
 * `public/sw.js` is a classic worker script: copied into the image verbatim, never through
 * a bundler, nothing to import. Writing the routing rules twice — once in TypeScript for
 * these tests and once in JavaScript for the browser — is the defect class this repository
 * keeps finding, a control described in one file and absent from the one that runs (D072).
 * So the harness below reads `public/sw.js` off disk, wraps it in a function whose
 * parameters shadow the worker globals, and runs it. Delete a clause from the worker and
 * these tests see the deletion.
 *
 * WHAT IS ACTUALLY UNDER TEST is one claim: **PHI cannot reach the Cache API.** The Cache
 * API is the same class of exposure as `localStorage` — an unencrypted, origin-scoped,
 * disk-backed store that survives the tab — and CLAUDE.md non-negotiable #4 and
 * docs/THREAT_MODEL.md's #1 ranked risk ("PHI accidentally cached") both land on it. The
 * design that makes the claim provable is that the cache is **written once, at install,
 * from a constant list**: there is no runtime write anywhere in the file, so a response
 * carrying a child's name has no path into storage. These tests assert that property from
 * several directions rather than reading the file and agreeing with it.
 *
 * The harness is held to the standard of the code it stands in for (D083): every fake does
 * only what the real API does, and says where it is narrower.
 */

const WEB_ROOT = path.resolve(__dirname, "..", "..");
const PUBLIC_DIR = path.join(WEB_ROOT, "public");
const SERVICE_WORKER_FILE = path.join(PUBLIC_DIR, "sw.js");

/** The origin the synthetic worker believes it is installed on. */
const ORIGIN = "https://blueshellspeech.test";

interface FakeRequest {
  method: string;
  url: string;
  /** `"navigate"` for a document load; `"cors"`, `"no-cors"` or `"same-origin"` otherwise. */
  mode: string;
}

function request(url: string, overrides: Partial<FakeRequest> = {}): FakeRequest {
  return {
    method: "GET",
    mode: "no-cors",
    ...overrides,
    url: url.startsWith("http") ? url : `${ORIGIN}${url}`,
  };
}

/** A document load — the shape the browser gives a top-level navigation. */
function navigation(url: string): FakeRequest {
  return request(url, { mode: "navigate" });
}

/**
 * Just enough `Response` to be stored, matched and read back.
 *
 * Hand-rolled rather than Node's global so the body is inspectable synchronously and so
 * `clone()` is observable — a runtime cache write is conventionally spelled
 * `cache.put(request, response.clone())`, and the tests want to see the clone as well as
 * the put.
 */
class FakeResponse {
  readonly body: string;
  readonly status: number;
  readonly headers: Record<string, string>;
  cloned = 0;

  constructor(body = "", init: { status?: number; headers?: Record<string, string> } = {}) {
    this.body = body;
    this.status = init.status ?? 200;
    this.headers = init.headers ?? {};
  }

  get ok(): boolean {
    return this.status >= 200 && this.status < 300;
  }

  clone(): FakeResponse {
    this.cloned += 1;
    return new FakeResponse(this.body, { status: this.status, headers: this.headers });
  }

  async text(): Promise<string> {
    return this.body;
  }
}

/** Cache keys compare by pathname, which is `cache.match`'s default behaviour. */
function cacheKey(target: string | FakeRequest): string {
  const url = typeof target === "string" ? target : target.url;
  return new URL(url, ORIGIN).pathname;
}

type Fetcher = (input: string | FakeRequest) => Promise<FakeResponse>;

class FakeCache {
  readonly entries = new Map<string, FakeResponse>();
  /** Every runtime write, in order. The PHI assertions require this to stay empty. */
  readonly puts: string[] = [];

  constructor(private readonly fetcher: Fetcher) {}

  async addAll(urls: string[]): Promise<void> {
    for (const url of urls) {
      const response = await this.fetcher(url);
      // The real `addAll` rejects the whole batch on any non-2xx, which is what turns a
      // precache list into a deploy-time assertion rather than a hope.
      if (!response.ok) throw new TypeError(`Request for ${url} failed with ${response.status}`);
      this.entries.set(cacheKey(url), response);
    }
  }

  async put(target: string | FakeRequest, response: FakeResponse): Promise<void> {
    this.puts.push(cacheKey(target));
    this.entries.set(cacheKey(target), response);
  }

  async match(target: string | FakeRequest): Promise<FakeResponse | undefined> {
    return this.entries.get(cacheKey(target));
  }

  async keys(): Promise<{ url: string }[]> {
    return [...this.entries.keys()].map((pathname) => ({ url: `${ORIGIN}${pathname}` }));
  }
}

class FakeCacheStorage {
  readonly stores = new Map<string, FakeCache>();

  constructor(private readonly fetcher: Fetcher) {}

  async open(name: string): Promise<FakeCache> {
    const existing = this.stores.get(name);
    if (existing) return existing;

    const created = new FakeCache(this.fetcher);
    this.stores.set(name, created);
    return created;
  }

  async keys(): Promise<string[]> {
    return [...this.stores.keys()];
  }

  async delete(name: string): Promise<boolean> {
    return this.stores.delete(name);
  }
}

/** The `ExtendableEvent` / `FetchEvent` surface `sw.js` actually touches. */
class FakeExtendableEvent {
  readonly extended: Promise<unknown>[] = [];

  waitUntil(promise: Promise<unknown>): void {
    this.extended.push(promise);
  }

  /** Resolves everything the handler asked the browser to wait for. */
  async settle(): Promise<void> {
    await Promise.all(this.extended);
  }
}

class FakeFetchEvent extends FakeExtendableEvent {
  responded: Promise<FakeResponse | undefined> | null = null;

  constructor(readonly request: FakeRequest) {
    super();
  }

  respondWith(promise: Promise<FakeResponse | undefined> | FakeResponse): void {
    this.responded = Promise.resolve(promise);
  }

  /** True when the worker took the request over. False means "the browser handles it". */
  get intercepted(): boolean {
    return this.responded !== null;
  }
}

interface WorkerScope {
  caches: FakeCacheStorage;
  /** Pathnames the worker asked the network for, in order. */
  fetched: string[];
  readonly skipWaitingCalls: number;
  readonly claimCalls: number;
  dispatch<E>(type: "install" | "activate" | "fetch", event: E): E;
}

interface LoadOptions {
  /**
   * Answers the worker's network calls. Throw to model the situation this feature exists
   * for: a phone in somebody's living room with no signal.
   */
  network?: (url: string) => Promise<FakeResponse> | FakeResponse;
}

/**
 * Evaluates `public/sw.js` in a synthetic scope and returns handles on what it did.
 *
 * The parameter names shadow the worker globals the file uses. In the browser those same
 * identifiers resolve to the real `ServiceWorkerGlobalScope`, so `sw.js` is written
 * exactly as it ships — no test-only branch, no injected seam.
 */
function loadServiceWorker(options: LoadOptions = {}): WorkerScope {
  const network =
    options.network ??
    ((url: string) =>
      new FakeResponse(`body of ${url}`, { headers: { "content-type": "text/html" } }));

  const fetched: string[] = [];

  const fetchImpl: Fetcher = async (input) => {
    const url = typeof input === "string" ? new URL(input, ORIGIN).toString() : input.url;
    fetched.push(new URL(url).pathname);
    return await network(url);
  };

  const caches = new FakeCacheStorage(fetchImpl);
  const listeners = new Map<string, (event: unknown) => void>();

  let skipWaitingCalls = 0;
  let claimCalls = 0;

  const scope = {
    addEventListener(type: string, handler: (event: unknown) => void) {
      listeners.set(type, handler);
    },
    location: new URL(`${ORIGIN}/sw.js`),
    registration: { scope: `${ORIGIN}/` },
    skipWaiting: async () => {
      skipWaitingCalls += 1;
    },
    clients: {
      claim: async () => {
        claimCalls += 1;
      },
    },
  };

  const source = readFileSync(SERVICE_WORKER_FILE, "utf8");

  const factory = new Function("self", "caches", "fetch", "Response", source) as (
    self: unknown,
    caches: FakeCacheStorage,
    fetch: Fetcher,
    Response: typeof FakeResponse,
  ) => void;

  factory(scope, caches, fetchImpl, FakeResponse);

  return {
    caches,
    fetched,
    get skipWaitingCalls() {
      return skipWaitingCalls;
    },
    get claimCalls() {
      return claimCalls;
    },
    dispatch<E>(type: "install" | "activate" | "fetch", event: E): E {
      const handler = listeners.get(type);
      if (!handler) throw new Error(`sw.js registered no "${type}" listener`);
      handler(event);
      return event;
    },
  };
}

/** Installs the worker and hands back the single cache it created. */
async function installed(options: LoadOptions = {}): Promise<{
  scope: WorkerScope;
  cacheName: string;
  cache: FakeCache;
  urls: string[];
}> {
  const scope = loadServiceWorker(options);

  const event = scope.dispatch("install", new FakeExtendableEvent());
  await event.settle();

  const names = await scope.caches.keys();
  const cacheName = names[0];
  if (names.length !== 1 || cacheName === undefined) {
    throw new Error(`install created ${names.length} cache(s): ${names.join(", ")}`);
  }

  const cache = await scope.caches.open(cacheName);
  const urls = (await cache.keys()).map((entry) => new URL(entry.url).pathname).sort();

  return { scope, cacheName, cache, urls };
}

/**
 * Every page in the authenticated route group, DISCOVERED RATHER THAN LISTED.
 *
 * A hard-coded list of PHI-bearing URLs is a test about the day it was written
 * (docs/TEST_STRATEGY.md, "a guard over a SET enumerates the set"): the page added next
 * slice is exactly the one nobody remembers to add. `app/(app)` is a route group, so the
 * routes are a fact about the directory and reading the directory is the only version that
 * stays true.
 *
 * Deliberately a second, independent walk of the same tree as the one in `e2e/auth.spec.ts`
 * — Playwright and Vitest resolve modules differently and sharing it would put a
 * runner-compatibility problem between this guard and the thing it guards.
 */
function authenticatedRoutes(): string[] {
  const group = path.join(WEB_ROOT, "app", "(app)");

  function walk(directory: string, segments: string[]): string[] {
    const found: string[] = [];

    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        // Route groups are parentheses in the filesystem and nothing in the URL.
        const next = /^\(.*\)$/.test(entry.name) ? segments : [...segments, entry.name];
        found.push(...walk(path.join(directory, entry.name), next));
        continue;
      }

      if (entry.name === "page.tsx") {
        found.push(
          "/" +
            segments
              .map((s) => (/^\[.*\]$/.test(s) ? "11111111-1111-1111-1111-111111111111" : s))
              .join("/"),
        );
      }
    }

    return found;
  }

  return walk(group, []).sort();
}

describe("install", () => {
  /**
   * The precache is the whole of what this origin's Cache API will ever hold.
   *
   * Control: `cache.addAll(PRECACHE)` in the install handler of `public/sw.js`.
   * Deleted → red, "AssertionError: expected [] to include '/offline.html'". The worker
   * still installs, and installs successfully, with an empty cache — which is the quiet
   * version of this failure: everything works until the signal drops.
   */
  it("precaches the offline shell and nothing else", async () => {
    const { urls } = await installed();

    expect(urls).toContain("/offline.html");
    expect(urls.length).toBeGreaterThan(1);
  });

  /**
   * Every precached URL is a file committed under `web/public`.
   *
   * This is the structural half of "PHI cannot be cached": `public/` holds compile-time
   * constants served statically, so a URL that resolves to a file in there cannot carry a
   * patient's name. A precache entry that does NOT resolve to one is either a typo — which
   * makes `addAll` reject and the whole worker fail to install, silently disabling the
   * offline shell — or somebody reaching for an application route.
   *
   * Control: the `PRECACHE` array in `public/sw.js`.
   * `"/offline.html"` replaced with `"/today"` → red, "AssertionError: /today is precached
   * but is not a file under web/public. Only compile-time static assets may be cached:
   * anything the application renders can carry PHI.: expected false to be true".
   */
  it("caches only files that exist in public/", async () => {
    const { urls } = await installed();

    for (const url of urls) {
      const onDisk = path.join(PUBLIC_DIR, url);

      expect(
        existsSync(onDisk),
        `${url} is precached but is not a file under web/public. Only compile-time ` +
          `static assets may be cached: anything the application renders can carry PHI.`,
      ).toBe(true);
    }
  });

  /**
   * No authenticated route is on the allowlist — checked against the routes that exist,
   * not against a list somebody has to extend.
   *
   * Control: the `PRECACHE` array in `public/sw.js`.
   * `"/offline.css"` replaced with `"/today"` → red, "AssertionError: /today is precached
   * AND is a page in app/(app). Routes discovered: /appointments/new, /dashboard,
   * /enquiries, /enquiries/11111111-…, /notes/11111111-…, /patients, /patients/11111111-…,
   * /patients/new, /today.: expected true to be false".
   */
  it("never precaches a page from the authenticated group", async () => {
    const routes = authenticatedRoutes();

    // A `for` over an empty array asserts nothing and this test stays green, so the walk
    // has to prove it found the group. Loose on purpose: an exact count is the list again.
    expect(routes.length, `Routes discovered: ${routes.join(", ")}`).toBeGreaterThan(5);

    const { urls } = await installed();

    for (const route of routes) {
      expect(
        urls.includes(route),
        `${route} is precached AND is a page in app/(app). Routes discovered: ` +
          `${routes.join(", ")}.`,
      ).toBe(false);
    }
  });

  /**
   * Everything the offline shell loads is precached with it.
   *
   * A shell whose stylesheet is not in the cache renders as unstyled markup — which is a
   * broken-looking version of the real UI, the one thing the offline screen is not allowed
   * to be. Derived from the HTML rather than asserted as a list, so a subresource added to
   * `offline.html` tomorrow arrives covered or arrives red.
   *
   * Control: the `PRECACHE` array in `public/sw.js`.
   * `"/offline.css"` deleted → red, "AssertionError: offline.html loads /offline.css, which
   * the worker does not precache — offline it would render unstyled. Precached:
   * /icons/icon.svg, /offline.html.: expected [ '/icons/icon.svg', '/offline.html' ] to
   * include '/offline.css'".
   */
  it("precaches every subresource the offline shell loads", async () => {
    const { urls } = await installed();
    const html = readFileSync(path.join(PUBLIC_DIR, "offline.html"), "utf8");

    const referenced = [...html.matchAll(/(?:href|src)="([^"]+)"/g)]
      .map((match) => match[1] ?? "")
      // Anchors to other pages are navigation, not a subresource; only root-relative asset
      // paths are things the browser must have in hand to render this screen.
      .filter((href) => href.startsWith("/") && /\.[a-z0-9]+$/i.test(href));

    expect(
      referenced.length,
      `No subresources found in offline.html. Either the shell stopped loading any, or ` +
        `this scan stopped working.`,
    ).toBeGreaterThan(0);

    for (const href of referenced) {
      expect(
        urls,
        `offline.html loads ${href}, which the worker does not precache — offline it ` +
          `would render unstyled. Precached: ${urls.join(", ")}.`,
      ).toContain(href);
    }
  });
});

describe("activate", () => {
  /**
   * Activation leaves exactly one cache on the origin: this worker's.
   *
   * That is a stronger guarantee than "clean up old versions". If anything ever did put a
   * clinical response into the Cache API — an experiment, a library, a future mistake —
   * the next activation removes it, and activation happens on every deploy.
   *
   * Control: the `keys.filter(...).map(...caches.delete...)` block in the activate handler.
   * Deleted → red, "AssertionError: expected [ 'blueshell-shell-v1', …(1) ] to deeply equal
   * [ 'blueshell-shell-v1' ]", with `+ "someone-elses-cache"` in the diff.
   */
  it("deletes every other cache on the origin", async () => {
    const { scope, cacheName } = await installed();

    const stray = await scope.caches.open("someone-elses-cache");
    await stray.put("/dashboard", new FakeResponse("<h1>Maya Fictional</h1>"));

    const event = scope.dispatch("activate", new FakeExtendableEvent());
    await event.settle();

    expect(await scope.caches.keys()).toEqual([cacheName]);
  });

  /**
   * The worker takes over pages that are already open.
   *
   * Without `clients.claim()` the tab that registered the worker is not controlled by it,
   * so the first offline navigation after install — the most likely one, because it is the
   * visit where Michelle installed the app — bypasses the worker and hits Safari's error
   * page instead of the shell.
   *
   * Control: `self.clients.claim()` in the activate handler.
   * Deleted → red, "expected +0 to be 1 // Object.is equality".
   */
  it("claims open clients", async () => {
    const { scope } = await installed();

    const event = scope.dispatch("activate", new FakeExtendableEvent());
    await event.settle();

    expect(scope.claimCalls).toBe(1);
  });
});

describe("navigations", () => {
  /**
   * Online, a navigation is the network's answer — not a cached copy of an earlier one.
   *
   * `app/(app)` is `force-dynamic` and every authenticated response carries `no-store`
   * (docs/THREAT_MODEL.md ranks a cached PHI page as the most likely accidental disclosure
   * in the system). A worker that answered navigations from storage would defeat both
   * without touching either.
   *
   * Control: `return await fetch(request)` in `respondToNavigation` in `public/sw.js`.
   * Replaced with the offline shell → red, "expected 'shell' to be 'today, live from the
   * server'".
   */
  it("serve the network's response, live", async () => {
    const { scope } = await installed({
      network: (url) =>
        url.endsWith("/today")
          ? new FakeResponse("today, live from the server")
          : new FakeResponse("shell"),
    });

    const event = scope.dispatch("fetch", new FakeFetchEvent(navigation("/today")));
    const response = await event.responded;

    expect(response?.body).toBe("today, live from the server");
  });

  /**
   * Offline, a navigation gets the shell — for ANY route, including one that has never
   * been visited. There is no per-page offline copy and there must never be one.
   *
   * Control: the `cache.match(OFFLINE_URL)` in `respondToNavigation`'s `catch` branch.
   * Deleted → red, "AssertionError: expected 'You are offline, and the offline scre…' to be
   * 'the offline shell'" — the last-resort plain-text Response is reached instead, which is
   * the worker admitting it has nothing rather than serving the screen this task ships.
   */
  it("fall back to the offline shell when the network is unreachable", async () => {
    let online = true;

    const { scope, cache } = await installed({
      network: (url) => {
        if (!online) throw new TypeError("network unavailable");
        return new FakeResponse(url.endsWith("/offline.html") ? "the offline shell" : "asset");
      },
    });

    // The signal goes after the worker has installed, which is the real sequence: the
    // shell is fetched on a working connection and read back on a broken one.
    online = false;

    const event = scope.dispatch("fetch", new FakeFetchEvent(navigation("/patients/abc")));
    const response = await event.responded;

    expect(response?.body).toBe("the offline shell");
    // The failed navigation itself must not have been cached on the way past.
    expect(cache.puts).toEqual([]);
  });

  /**
   * THE ONE THAT MATTERS. Navigating every authenticated page in the application leaves
   * the cache byte-for-byte as install left it.
   *
   * Driven off the route walk, so the page added in slice 7 is covered without anybody
   * remembering. The responses are given `Cache-Control: no-store` and a synthetic
   * patient name, which is what a real one would carry.
   *
   * Control: the ABSENCE of a runtime write in `respondToNavigation`. Adding the
   * conventional `const cache = await caches.open(CACHE_NAME); cache.put(request,
   * response.clone());` before the return — the network-first-with-cache-fallback recipe
   * every service-worker tutorial teaches — → red, "AssertionError: 9 authenticated
   * navigation(s) wrote to the Cache API: /appointments/new, /dashboard, /enquiries,
   * /enquiries/11111111-…, /notes/11111111-…, /patients, /patients/11111111-…,
   * /patients/new, /today. Nothing the application renders may be cached.: expected
   * [ '/appointments/new', …(8) ] to deeply equal []".
   */
  it("write nothing to the cache, for any route in the authenticated group", async () => {
    const routes = authenticatedRoutes();
    expect(routes.length, `Routes discovered: ${routes.join(", ")}`).toBeGreaterThan(5);

    const { scope, cache, urls } = await installed({
      network: () =>
        new FakeResponse("<h1>Maya Fictional — synthetic</h1>", {
          headers: { "cache-control": "no-store" },
        }),
    });

    for (const route of routes) {
      const event = scope.dispatch("fetch", new FakeFetchEvent(navigation(route)));
      await event.responded;
    }

    expect(
      cache.puts,
      `${routes.length} authenticated navigation(s) wrote to the Cache API: ` +
        `${cache.puts.join(", ")}. Nothing the application renders may be cached.`,
    ).toEqual([]);

    const after = (await cache.keys()).map((entry) => new URL(entry.url).pathname).sort();
    expect(after).toEqual(urls);
  });
});

describe("requests the worker refuses to handle", () => {
  /**
   * A non-GET is left entirely alone.
   *
   * Server actions and the BFF's POSTs are how clinical content is written. A worker that
   * intercepted one could answer it from storage, retry it, or — the version that actually
   * happens — swallow the failure and hand back the offline shell as though a signature
   * had succeeded.
   *
   * Control: `if (request.method !== "GET") return;` in the fetch handler.
   * Deleted → red, "a POST to /notes/abc was intercepted by the service worker: expected
   * true to be false".
   */
  it("ignores every method except GET", async () => {
    const { scope } = await installed();

    for (const method of ["POST", "PUT", "DELETE", "PATCH"]) {
      const event = scope.dispatch(
        "fetch",
        new FakeFetchEvent(request("/notes/abc", { method, mode: "navigate" })),
      );

      expect(
        event.intercepted,
        `a ${method} to /notes/abc was intercepted by the service worker`,
      ).toBe(false);
    }
  });

  /**
   * Cross-origin is left alone too.
   *
   * THE FIRST VERSION OF THIS TEST STAYED GREEN WITH ITS CONTROL DELETED, which is the
   * whole reason the deletion is run rather than reasoned about (docs/TEST_STRATEGY.md).
   * It used a blob-storage URL — `…/session-audio/take-1`, the shape slice 2.5 will
   * produce — and that request is refused by the allowlist as well: its pathname is not in
   * `PRECACHE`, so it falls through whether or not the origin is checked. Two controls
   * covering for each other, and the test was measuring the wrong one.
   *
   * The two cases below are the ones only the origin check refuses:
   *
   *   a path COLLISION — `/offline.css` on somebody else's host. Without the check the
   *   pathname is on the allowlist, so a third party's asset is answered out of our cache;
   *
   *   a cross-origin NAVIGATION — a link out of the app. Without the check, a dead network
   *   hands back Blue Shell's offline screen under another domain's URL.
   *
   * Control: `if (url.origin !== self.location.origin) return;` in the fetch handler.
   * Deleted → red, "https://cdn.example.test/offline.css was intercepted by the service
   * worker: expected true to be false".
   */
  it("ignores requests to other origins", async () => {
    const { scope } = await installed();

    const foreign = [
      request("https://cdn.example.test/offline.css"),
      request("https://health.maryland.gov/programs", { mode: "navigate" }),
    ];

    for (const target of foreign) {
      const event = scope.dispatch("fetch", new FakeFetchEvent(target));

      expect(event.intercepted, `${target.url} was intercepted by the service worker`).toBe(
        false,
      );
    }
  });

  /**
   * A same-origin asset that is not on the allowlist is left to the browser.
   *
   * This is the difference between an allowlist and a catch-all. `/_next/static/...` is
   * harmless and could safely be cached; `/api/...` is the BFF and cannot. The worker
   * distinguishes neither, because it does not handle either — a rule with no exceptions
   * cannot acquire one by accident when a route is added.
   *
   * Control: the `PRECACHE.includes(url.pathname)` condition in the fetch handler.
   * Replaced with `true` → red, "/api/health was intercepted by the service worker:
   * expected true to be false".
   */
  it("ignores same-origin requests that are not on the allowlist", async () => {
    const { scope } = await installed();

    for (const url of ["/api/health", "/_next/static/chunks/main.js", "/img/headshot-480.webp"]) {
      const event = scope.dispatch("fetch", new FakeFetchEvent(request(url)));

      expect(event.intercepted, `${url} was intercepted by the service worker`).toBe(false);
    }
  });
});

describe("precached assets", () => {
  /**
   * The shell's own assets come from the cache, which is the only reason any of this
   * works with no signal.
   *
   * Control: the `PRECACHE.includes(url.pathname)` condition in the fetch handler.
   * Replaced with `false` → red, "AssertionError: expected false to be true" on
   * `event.intercepted` — the worker declines the request, the browser goes to a network
   * that is not there, and the shell renders unstyled.
   */
  it("are served from the cache when the network is gone", async () => {
    let online = true;

    const { scope } = await installed({
      network: (url) => {
        if (!online) throw new TypeError("network unavailable");
        return new FakeResponse(`body of ${url}`);
      },
    });

    online = false;

    const event = scope.dispatch("fetch", new FakeFetchEvent(request("/offline.css")));
    const response = await event.responded;

    expect(event.intercepted).toBe(true);
    expect(response?.body).toContain("/offline.css");
  });
});
