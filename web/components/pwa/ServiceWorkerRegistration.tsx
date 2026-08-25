"use client";

import { useEffect } from "react";
import { registerServiceWorker } from "@/lib/pwa/register";

/**
 * Registers the service worker. Renders nothing.
 *
 * WHY A COMPONENT AT ALL. A worker has to be registered by script running on a page. The
 * alternatives are an inline `<script>` in the layout — which needs a CSP nonce, and
 * D042's `unsafe-inline` deviation is scoped to the marketing pages and is not being
 * extended — or bolting a `useEffect` onto some real component, which ties the lifetime of
 * the worker to the lifetime of unrelated UI. This is the smallest honest version: one
 * client island whose only job is named in its filename.
 *
 * WHY IT IS IN THE ROOT LAYOUT rather than behind the login. The worker's scope is the
 * whole origin, so it has to be registered by a page the browser will actually load: the
 * offline shell covers `/login` and the manifest's `start_url`, and an installed app whose
 * session has expired lands on `/login` first. Registering only inside `app/(app)` would
 * also make the wiring unreachable from the E2E suite, which has no session — and a
 * perfect service worker that nothing ever installs is the failure this component exists
 * to prevent.
 *
 * WHAT IT COSTS. Every page of the public marketing site now carries this island and, on
 * first visit, three small precache requests after hydration. Measured against the
 * alternative — no offline screen, and no installable app on the phone the product is for
 * — that is the right side of the trade, but it is a real cost and D038's cold-start work
 * is the reason it is worth naming.
 */
export function ServiceWorkerRegistration() {
  useEffect(() => {
    /*
     * No deferral to `window.load`. The usual reason to wait is to keep the precache from
     * competing with the page's own assets; here the precache is three files totalling a
     * few kilobytes, and an effect already runs after hydration. A `load` listener would
     * add a branch for the already-loaded case that would itself need testing, for no
     * measurable gain (frontend rules §15: is this complexity justified today?).
     *
     * `void` because the promise never rejects — see `lib/pwa/register.ts`.
     */
    void registerServiceWorker();
  }, []);

  return null;
}
