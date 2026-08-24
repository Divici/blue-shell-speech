import type { MetadataRoute } from "next";

/**
 * The public site should be indexed. Everything behind authentication must not be.
 *
 * `/login` and `/app/*` are excluded explicitly rather than relying on the fact that a
 * crawler cannot sign in — a disallow line is a statement of intent that survives someone
 * later adding a publicly reachable authenticated route by mistake.
 */
export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: "*",
      allow: "/",
      disallow: ["/login", "/app/", "/api/"],
    },
    sitemap: "https://blueshellspeech.com/sitemap.xml",
  };
}
