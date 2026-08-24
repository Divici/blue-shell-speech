import type { MetadataRoute } from "next";

/**
 * Only the public pages. The consultation form matters most here: a parent searching
 * for a speech therapist should be able to land directly on it.
 */
export default function sitemap(): MetadataRoute.Sitemap {
  const base = "https://blueshellspeech.com";
  const lastModified = new Date();

  return [
    { url: base, lastModified, changeFrequency: "monthly", priority: 1 },
    { url: `${base}/consultation`, lastModified, changeFrequency: "monthly", priority: 0.9 },
  ];
}
