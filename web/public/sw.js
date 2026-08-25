/*
 * Blue Shell Speech — service worker.
 *
 * ---------------------------------------------------------------------------------------
 * THIS WORKER MUST NEVER CACHE PHI. Everything below is arranged around that one sentence.
 * ---------------------------------------------------------------------------------------
 *
 * The Cache API is unencrypted, origin-scoped, disk-backed storage that outlives the tab —
 * the same class of exposure as `localStorage`, which CLAUDE.md non-negotiable #4 bans
 * outright, and the same one docs/THREAT_MODEL.md ranks first ("PHI accidentally cached").
 * `Cache-Control: no-store` does not reach it: `no-store` governs the HTTP cache and CDN
 * edges, and says nothing about what a script chooses to write into storage. So the
 * protection here is structural rather than a rule somebody has to remember:
 *
 *   1. THE CACHE IS WRITTEN EXACTLY ONCE, AT INSTALL, FROM A CONSTANT.
 *      `cache.addAll(PRECACHE)` in the install handler is the only write in this file.
 *      There is no `cache.put` anywhere — not on success, not on a fallback, not "just for
 *      the static chunk". A response from the network has no path into storage, so no
 *      response CAN carry a patient's name into storage.
 *
 *   2. THE FETCH HANDLER IS AN ALLOWLIST, NOT A CATCH-ALL.
 *      A same-origin GET is only answered from the cache if its pathname is literally in
 *      `PRECACHE`. Everything else — `/api/*`, `/_next/*`, every page in `app/(app)` — is
 *      left alone: no `respondWith`, so the browser behaves as though no worker existed.
 *      A route added next slice cannot fall into a handler by accident, because there is
 *      no handler for it to fall into.
 *
 *   3. ACTIVATION LEAVES ONE CACHE ON THE ORIGIN.
 *      Every other cache is deleted, not just older versions of this one. If anything ever
 *      does write clinical content into the Cache API, the next deploy removes it.
 *
 * The cost is stated plainly: this app is NOT offline-capable in the usual sense. Offline
 * you get one honest screen, not a working copy of the schedule. That is the intended
 * trade — the alternative is a device holding a copy of a child's record for as long as the
 * browser feels like keeping it.
 *
 * Everything here is reached through `self`, `caches`, `fetch` and `Response`, which is how
 * a classic worker script sees them. `lib/pwa/service-worker.test.ts` runs THIS FILE in a
 * synthetic scope, so the tests read the bytes the browser will.
 */

/**
 * Bump when PRECACHE changes.
 *
 * Activation deletes every cache that is not this one, so a bump is also the eviction: the
 * previous shell is gone the moment the new worker activates.
 */
const CACHE_NAME = "blueshell-shell-v1";

/**
 * THE ALLOWLIST. The complete contents of this origin's Cache API, forever.
 *
 * Every entry is a file committed under `web/public` — a compile-time constant served
 * statically, with no request context and nothing to interpolate. That property is what
 * makes "no PHI in the cache" checkable rather than aspirational, and
 * `lib/pwa/service-worker.test.ts` checks it: each entry must resolve to a real file in
 * `public/`, and none may match a page in `app/(app)`.
 *
 * Deliberately no Next build output. `/_next/static/*` is content-hashed, so precaching it
 * would mean regenerating this list on every build and invalidating the whole cache on
 * every deploy — and the offline shell is hand-written precisely so it does not depend on
 * a chunk whose name changes.
 */
const PRECACHE = ["/offline.html", "/offline.css", "/icons/icon.svg"];

/** The one screen this worker can produce without a network. */
const OFFLINE_URL = "/offline.html";

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(CACHE_NAME)
      // `addAll` rejects the whole batch on any non-2xx, so a typo in PRECACHE fails the
      // install loudly instead of leaving a worker that half works.
      .then((cache) => cache.addAll(PRECACHE))
      /*
       * Take over immediately rather than waiting for every tab to close.
       *
       * Normally `skipWaiting` is risky: a page already running against version N can find
       * itself served version N+1's chunks mid-session. It is safe here BECAUSE of the
       * design above — this worker serves three static files and answers nothing else, so
       * there is no version skew to create. The alternative is worse: an installed app
       * that keeps last month's offline copy until Michelle happens to close it.
       */
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))))
      /*
       * Claim pages that were already open.
       *
       * Without this, the tab that registered the worker is not controlled by it until the
       * next navigation — so the first time the signal drops, on the very visit where the
       * app was installed, the fallback below is not in the path at all.
       */
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;

  /*
   * Only GET. A server action, a note save, a sign-off — every write in this application
   * is a POST, and a worker with an opinion about one can answer it from storage, replay
   * it, or swallow its failure and hand back the offline shell as though it had worked.
   */
  if (request.method !== "GET") return;

  const url = new URL(request.url);

  /*
   * Only this origin. Slice 2.5 uploads session audio to a blob container on another
   * origin under a short-lived SAS; those requests carry a recording of a therapy session
   * and are none of this worker's business.
   */
  if (url.origin !== self.location.origin) return;

  if (request.mode === "navigate") {
    event.respondWith(respondToNavigation(request));
    return;
  }

  if (PRECACHE.includes(url.pathname)) {
    event.respondWith(respondFromCache(request));
    return;
  }

  /*
   * Everything else falls through with no `respondWith`, which hands the request back to
   * the browser untouched. That includes every authenticated page and every BFF route —
   * they are not excluded by a denylist that somebody has to maintain; they are simply not
   * on the allowlist above.
   */
});

/**
 * Navigations: the network's answer, live, or the offline shell.
 *
 * The network response is returned as it arrives and is NOT stored. `app/(app)` is
 * `force-dynamic` and every authenticated response carries `no-store`; a worker that kept
 * a copy would defeat both without touching either, and would then serve a clinician's
 * dashboard to whoever opened the app next.
 */
async function respondToNavigation(request) {
  try {
    return await fetch(request);
  } catch {
    const cache = await caches.open(CACHE_NAME);
    const shell = await cache.match(OFFLINE_URL);

    /*
     * The shell is precached at install, so it is there unless installation failed. If it
     * somehow is not, say so in text rather than returning `undefined` — which the browser
     * renders as its own network-error page, and the point of this screen is that the
     * product explains itself instead of the browser doing it badly.
     */
    return (
      shell ??
      new Response("You are offline, and the offline screen could not be loaded.", {
        status: 503,
        headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" },
      })
    );
  }
}

/**
 * Allowlisted static assets: cache first, network as the fallback.
 *
 * Cache-first because the whole reason these three files are stored is that the network is
 * gone. The network fallback covers the window between a version bump and the new worker
 * activating. Nothing is written back either way.
 */
async function respondFromCache(request) {
  const cache = await caches.open(CACHE_NAME);
  const cached = await cache.match(request);
  if (cached) return cached;

  return await fetch(request);
}
