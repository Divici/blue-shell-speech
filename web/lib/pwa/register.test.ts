import { describe, it, expect, vi, afterEach } from "vitest";
import {
  SERVICE_WORKER_SCOPE,
  SERVICE_WORKER_URL,
  registerServiceWorker,
  serviceWorkerSupported,
} from "./register";

/**
 * Registration.
 *
 * Everything downstream of this file depends on it succeeding — the offline shell, and the
 * install prompt in WORK_QUEUE 2.2, which cannot offer an install a browser will not honour
 * without a controlling worker. Everything else in the application depends on it FAILING
 * QUIETLY: a worker that cannot register must leave a working online app behind, because
 * the alternative is a clinician who cannot open a patient record because a caching layer
 * she does not know exists refused to install.
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

/** A `navigator` with a service-worker container, as every supporting browser has. */
function navigatorWith(register: ReturnType<typeof vi.fn>): void {
  vi.stubGlobal("navigator", { serviceWorker: { register } });
}

describe("serviceWorkerSupported", () => {
  /**
   * iOS Safari has no `serviceWorker` in a private window, and neither does any browser on
   * an insecure origin. Both are ordinary states, not errors.
   *
   * Control: the `"serviceWorker" in nav` check in `serviceWorkerSupported`.
   * Replaced with `return true` → red, "expected true to be false".
   */
  it("is false when the browser has no service-worker container", () => {
    vi.stubGlobal("navigator", {});

    expect(serviceWorkerSupported()).toBe(false);
  });

  it("is true when it does", () => {
    navigatorWith(vi.fn());

    expect(serviceWorkerSupported()).toBe(true);
  });
});

describe("registerServiceWorker", () => {
  /**
   * The worker is registered at the ROOT scope, from a file at the root.
   *
   * Scope is decided by where the script is served from, and a worker registered at, say,
   * `/_next/sw.js` would control nothing outside `/_next`. The offline shell has to cover
   * `/today`, `/login` and the manifest's `start_url`, so the whole origin is the scope,
   * and it is stated rather than inherited.
   *
   * Control: the `{ scope: SERVICE_WORKER_SCOPE }` argument in `registerServiceWorker`.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" to be called with arguments:
   * [ '/sw.js', { scope: '/' } ]", with the received call showing `'/sw.js'` alone.
   */
  it("registers /sw.js at the root scope", async () => {
    const register = vi.fn().mockResolvedValue({ scope: "https://example.test/" });
    navigatorWith(register);

    const registration = await registerServiceWorker();

    expect(register).toHaveBeenCalledWith(SERVICE_WORKER_URL, { scope: SERVICE_WORKER_SCOPE });
    expect(registration).not.toBeNull();
    expect(SERVICE_WORKER_URL).toBe("/sw.js");
    expect(SERVICE_WORKER_SCOPE).toBe("/");
  });

  /**
   * TWO CLAUSES COVER FOR EACH OTHER HERE, and the deletion is what found it.
   *
   * The obvious control is the `if (!serviceWorkerSupported(nav)) return null` guard.
   * Neutering it on its own leaves this test GREEN: `nav.serviceWorker` is `undefined`,
   * `.register` throws a `TypeError` inside the `try`, and the `catch` returns null — the
   * same answer by a worse route. So the guard is not the control this test names; the
   * pair is (docs/TEST_STRATEGY.md, and `An_empty_signed_note_cannot_be_deleted_by_raw_sql`
   * for the same shape one tier over).
   *
   * The guard stays because routing an ordinary, expected browser capability through a
   * thrown `TypeError` is bad code, not because it is load-bearing here — and its own
   * behaviour IS falsifiable, in the `serviceWorkerSupported` tests above.
   *
   * Control: the guard and the `catch`, together.
   * Guard replaced with `if (false)` AND the `catch` rewritten to rethrow → red,
   * "AssertionError: promise rejected \"TypeError: Cannot read properties of unde…\"
   * instead of resolving. Caused by: TypeError: Cannot read properties of undefined
   * (reading 'register')" — thrown from an effect in the root layout, so on every page of
   * the public site in any browser without a worker container.
   */
  it("returns null rather than throwing where workers do not exist", async () => {
    vi.stubGlobal("navigator", {});

    await expect(registerServiceWorker()).resolves.toBeNull();
  });

  /**
   * A registration can be refused for reasons that have nothing to do with this code — a
   * user who has disabled storage, an enterprise policy, a MIME type a proxy rewrote. None
   * of them is a reason for the page not to work.
   *
   * The other half of the pair above, isolated: here the guard passes — `serviceWorker`
   * exists — so the `catch` is the only thing standing between a refusal and an unhandled
   * rejection.
   *
   * Control: the `catch` around `register(...)`.
   * Rewritten to rethrow → red, "AssertionError: promise rejected \"Error: SecurityError:
   * The operation is in…\" instead of resolving. Caused by: Error: SecurityError: The
   * operation is insecure."
   */
  it("returns null rather than throwing when the browser refuses", async () => {
    const register = vi.fn().mockRejectedValue(new Error("SecurityError: The operation is insecure."));
    navigatorWith(register);

    await expect(registerServiceWorker()).resolves.toBeNull();
  });
});
