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
