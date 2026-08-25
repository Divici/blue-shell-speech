/**
 * Service worker registration.
 *
 * Split out from the component that calls it so the decisions are testable without a DOM,
 * and so WORK_QUEUE 2.2 has one function to await when it needs to know whether the app is
 * installable before offering to install it.
 *
 * Two rules shape everything here:
 *
 *   THE SCOPE IS THE WHOLE ORIGIN. A worker's scope is decided by where its script is
 *   served from, so `sw.js` sits at the root. The offline shell has to cover `/login` and
 *   the manifest's `start_url` as well as the app, and a worker registered under a
 *   subdirectory would control neither.
 *
 *   FAILING TO REGISTER IS NOT AN ERROR. Private browsing, a disabled storage setting, an
 *   enterprise policy, an insecure origin, a proxy that rewrote the MIME type — all of
 *   them produce a browser with no worker, and all of them must leave a working online
 *   application behind. A clinician who cannot open a patient record because a caching
 *   layer she does not know exists refused to install is the worst possible outcome of a
 *   feature whose entire purpose is resilience.
 */

/** The worker script, at the root so its default scope is the origin. */
export const SERVICE_WORKER_URL = "/sw.js";

/** Stated rather than inherited, so moving the file cannot silently narrow the scope. */
export const SERVICE_WORKER_SCOPE = "/";

/**
 * Whether this browser has a service-worker container at all.
 *
 * iOS Safari does not, in a private window; nor does any browser on an insecure origin.
 * Both are ordinary states rather than errors, and both are the reason every call below
 * checks before it reaches for `navigator.serviceWorker`.
 */
export function serviceWorkerSupported(nav: Navigator = navigator): boolean {
  return typeof nav === "object" && nav !== null && "serviceWorker" in nav;
}

/**
 * Registers the worker, or returns `null` if it could not be registered.
 *
 * Never throws. The caller is an effect in the root layout, so a rejection here would
 * surface as an unhandled error on every page of the public site.
 */
export async function registerServiceWorker(
  nav: Navigator = navigator,
): Promise<ServiceWorkerRegistration | null> {
  if (!serviceWorkerSupported(nav)) return null;

  try {
    return await nav.serviceWorker.register(SERVICE_WORKER_URL, {
      scope: SERVICE_WORKER_SCOPE,
    });
  } catch {
    /*
     * Swallowed deliberately, and swallowed silently: the browser has already logged the
     * real reason to the console, and this application's own logs must never carry
     * anything from a page that might be rendering PHI (CLAUDE.md non-negotiable #3).
     */
    return null;
  }
}
