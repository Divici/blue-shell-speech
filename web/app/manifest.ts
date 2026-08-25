import type { MetadataRoute } from "next";
import { palette } from "@/lib/design-tokens";

/**
 * The web app manifest, served at `/manifest.webmanifest` and linked from every page.
 *
 * WHY THIS MATTERS MORE THAN IT LOOKS. Installing is not a convenience here. **Only a
 * home-screen-installed PWA escapes iOS Safari's 7-day storage eviction**
 * (docs/ARCHITECTURE.md, "iOS realities"): an encrypted dictation draft left in a browser
 * tab is deleted after a week of not opening the site, and the same draft in an installed
 * app is not. A manifest that quietly fails the installability criteria therefore degrades
 * the durability of clinical audio, with nothing in the UI to say so — which is why
 * `app/manifest.test.ts` checks each field against a browser's requirements rather than
 * against what looked right.
 *
 * WORK_QUEUE 2.2 owns the install prompt and the standalone detection. This is the part
 * that has to be true first.
 */
export default function manifest(): MetadataRoute.Manifest {
  return {
    /*
     * The app's stable identity. Without it a browser derives the id from `start_url`, so
     * changing where the app opens would look like a different app and orphan the one
     * already on Michelle's home screen.
     */
    id: "/",

    name: "Blue Shell Speech",
    // Home-screen labels truncate hard — iOS shows about twelve characters.
    short_name: "Blue Shell",
    description:
      "Practice tools for Blue Shell Speech: the day's visits, patient records, and " +
      "post-session dictation.",

    /*
     * The installed app opens on the day's visits, which is the screen that is useful
     * between two houses. Signed out this redirects to `/login`, which is correct: making
     * `start_url` the login page would cost a redirect on every launch where the session
     * is still good.
     */
    start_url: "/today",

    /*
     * The whole origin. The offline shell has to cover `/login` and the marketing pages
     * as well as the app — an installed app that lands on Safari's own error page has
     * told the user nothing.
     */
    scope: "/",

    /*
     * Standalone, not `browser`. In a browser tab the 7-day eviction still applies, so
     * `display: "browser"` would be an install that installs nothing that matters.
     */
    display: "standalone",

    /*
     * No `orientation` lock. Michelle holds the phone however she is holding it, and
     * pinning an orientation breaks the user's own rotation lock — an accessibility
     * regression for anybody using a device in a mount or a stand.
     */

    theme_color: palette.blue,
    background_color: palette.mist,

    icons: [
      /*
       * SVG first, for browser chrome that will take one: it is 1.6 KB and sharp at every
       * size. The PNGs below are what Android and iOS actually install.
       */
      { src: "/icons/icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any" },
      { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      /*
       * The adaptive icon. Without a `maskable` entry Android letterboxes the mark inside
       * a white circle — the "sticker" look that says an app was ported rather than built.
       */
      {
        src: "/icons/icon-maskable-512.png",
        sizes: "512x512",
        type: "image/png",
        purpose: "maskable",
      },
    ],
  };
}
