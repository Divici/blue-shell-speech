import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /*
   * Standalone output traces only the files actually needed at runtime, which keeps the
   * image small. Image size is cold-start time here: both containers scale to zero
   * (DECISIONS.md D001), so every megabyte is paid on the first request after idle by a
   * parent loading the homepage.
   */
  output: "standalone",

  poweredByHeader: false,
  reactStrictMode: true,

  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          {
            /*
             * Content-Security-Policy for the PUBLIC site.
             *
             * docs/SECURITY.md specifies a nonce-based policy with no `unsafe-inline`.
             * That is the right policy for the authenticated app and it is NOT
             * implementable here, for a concrete reason:
             *
             *   A nonce must be unique per response. Generating one requires middleware
             *   and forces every page to render dynamically — which would discard the
             *   static prerendering these pages depend on, and defeat the edge caching
             *   that DECISIONS.md D038 chose to fix a 22-second cold start.
             *
             * Next.js hydration emits inline bootstrap scripts, so without a nonce the
             * options are `unsafe-inline` or a broken page.
             *
             * Accepted here because the public site renders NO user-generated content and
             * holds no PHI: every string on it is a compile-time constant from
             * lib/site-content.ts. The XSS surface a nonce would defend is empty.
             *
             * The authenticated app is dynamic by necessity, so it gets the nonce-based
             * policy with no exception — tracked for slice 2.
             */
            key: "Content-Security-Policy",
            value: [
              "default-src 'self'",
              "script-src 'self' 'unsafe-inline'",
              /*
               * The service worker, allowed DELIBERATELY rather than by inheritance.
               *
               * `worker-src` is absent by default and falls back to `script-src` — which
               * carries `unsafe-inline` for the reason stated above. Stating it here is a
               * tightening, not a loosening: the worker may be loaded from this origin
               * and from nothing else, and no inline source can ever become one. The
               * D042 deviation stays where it is and is not extended to a new context.
               */
              "worker-src 'self'",
              /*
               * Same reasoning for the manifest, which otherwise falls back to
               * `default-src`. An installable app is a new fetch the browser makes on its
               * own initiative; it gets its own line.
               */
              "manifest-src 'self'",
              "style-src 'self' 'unsafe-inline'",
              "img-src 'self' data:",
              "font-src 'self'",
              "connect-src 'self'",
              "form-action 'self'",
              "frame-ancestors 'none'",
              "base-uri 'self'",
              "object-src 'none'",
              /*
               * `upgrade-insecure-requests` is deliberately ABSENT.
               *
               * It rewrites every subresource request to https — including on an http
               * origin. Chromium exempts localhost; WebKit does not, so on
               * http://localhost every script, stylesheet and font failed with an SSL
               * error, the page never hydrated, and every form test broke. Caught by the
               * WebKit E2E lane; invisible in Chromium.
               *
               * It also buys nothing here. HSTS with a two-year max-age and preload
               * already guarantees the browser never issues an http request to this
               * origin in production, and the page loads no cross-origin subresources at
               * all — `default-src 'self'` above is the actual control.
               */
            ].join("; "),
          },
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
          { key: "X-Frame-Options", value: "DENY" },
          {
            key: "Strict-Transport-Security",
            value: "max-age=63072000; includeSubDomains; preload",
          },
          {
            /*
             * The microphone is the one capability this app needs, for dictation.
             * Everything else is denied rather than left at the browser default.
             */
            key: "Permissions-Policy",
            value: "geolocation=(), camera=(), microphone=(self), payment=()",
          },
        ],
      },
      {
        /*
         * The service worker script must revalidate on every update check.
         *
         * Next serves `public/` with a cacheable default. A worker script the browser is
         * allowed to reuse is a worker the browser keeps running: an update that changes
         * the offline shell, or the precache allowlist, would not reach an installed app
         * until the stored copy expired. `max-age=0, must-revalidate` makes every update
         * check a real request while still allowing a 304.
         *
         * This is additive to the `/:path*` block above — Next applies both, and this one
         * only sets a key that block does not.
         */
        source: "/sw.js",
        headers: [{ key: "Cache-Control", value: "public, max-age=0, must-revalidate" }],
      },
    ];
  },
};

export default nextConfig;
