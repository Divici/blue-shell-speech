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
              "style-src 'self' 'unsafe-inline'",
              "img-src 'self' data:",
              "font-src 'self'",
              "connect-src 'self'",
              "form-action 'self'",
              "frame-ancestors 'none'",
              "base-uri 'self'",
              "object-src 'none'",
              "upgrade-insecure-requests",
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
    ];
  },
};

export default nextConfig;
