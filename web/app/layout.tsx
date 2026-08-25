import type { Metadata, Viewport } from "next";
import { Playfair_Display, Inter } from "next/font/google";
import { ServiceWorkerRegistration } from "@/components/pwa/ServiceWorkerRegistration";
import "./globals.css";

/*
 * Typography per comp 2: Playfair Display for headings, Inter for body.
 *
 * next/font self-hosts both at build time — no request to Google's servers at runtime.
 * That removes a third-party connection from every page load (faster, and one fewer
 * vendor seeing visitor IPs on a healthcare site) and eliminates the layout shift that
 * an external font swap would otherwise cause.
 *
 * `display: swap` shows fallback text immediately rather than blocking paint.
 */
const playfair = Playfair_Display({
  variable: "--font-playfair",
  subsets: ["latin"],
  display: "swap",
  // 700 only. Every display heading in the site uses `font-bold`; weight 600 was
  // requested and never referenced, costing a second font file for nothing. Fonts are
  // the largest payload on this page, so an unused weight is the most expensive kind
  // of dead code here.
  weight: ["700"],
});

const inter = Inter({
  variable: "--font-inter",
  subsets: ["latin"],
  display: "swap",
});

export const metadata: Metadata = {
  title: {
    default: "Blue Shell Speech | Pediatric Speech Therapy in Maryland",
    template: "%s | Blue Shell Speech",
  },
  description:
    "In-home speech-language therapy for children birth to 5 years in Maryland. " +
    "Personalized, play-based care that supports growth, confidence, and everyday communication.",
  openGraph: {
    title: "Blue Shell Speech",
    description:
      "In-home speech-language therapy for children birth to 5 years in Maryland.",
    type: "website",
    locale: "en_US",
  },
  robots: { index: true, follow: true },

  /*
   * The iOS home screen, which reads its own metadata rather than the manifest's.
   *
   * `apple-touch-icon` is the home-screen image — Safari does not use the manifest's
   * `icons` — and `apple-mobile-web-app-title` is the label under it, in place of
   * `short_name`. Michelle's phone is the deployment target, so on iOS these are not a
   * fallback for the manifest; they are the manifest.
   *
   * WHAT THIS DOES NOT DO is put the launched app in its own window. Next 16 renders
   * `appleWebApp.capable` as the standardised `mobile-web-app-capable`, not the legacy
   * `apple-mobile-web-app-capable`; the tag that decides standalone display on current iOS
   * is `display: "standalone"` in `app/manifest.ts`, which is where that claim is asserted.
   * Worth being exact about, because standalone display is what escapes Safari's 7-day
   * storage eviction (docs/ARCHITECTURE.md) — an "installed" app that is still a tab is
   * still evicted, and a comment claiming the wrong control here would be the last place
   * anybody looked.
   *
   * `statusBarStyle` is left at `default`. `black-translucent` runs content under the
   * status bar, which on a phone used one-handed in a car park puts the record button
   * where the clock is.
   */
  appleWebApp: {
    capable: true,
    title: "Blue Shell",
    statusBarStyle: "default",
  },

  icons: {
    icon: [
      { url: "/icons/icon.svg", type: "image/svg+xml" },
      { url: "/icons/icon-192.png", sizes: "192x192", type: "image/png" },
    ],
    // Opaque and full-bleed: Safari rounds this rather than compositing it, so
    // transparent corners land on the home screen as black ones.
    apple: [{ url: "/icons/apple-touch-icon.png", sizes: "180x180", type: "image/png" }],
  },
};

export const viewport: Viewport = {
  themeColor: "#2D7FF9",
  width: "device-width",
  initialScale: 1,
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      className={`${playfair.variable} ${inter.variable} h-full antialiased`}
    >
      <body className="min-h-full flex flex-col">
        {/*
          Skip link, first in the DOM. A keyboard user should not have to tab through
          the full navigation on every page to reach the content.
        */}
        <a
          href="#main"
          className="skip-link bg-white text-navy font-semibold px-4 py-2 rounded-lg shadow-lg"
        >
          Skip to main content
        </a>
        {/*
          Registers the service worker. Renders nothing, and is here rather than in the
          authenticated layout because the worker's scope is the whole origin — the offline
          shell has to cover `/login` and the manifest's `start_url` too.
        */}
        <ServiceWorkerRegistration />
        {children}
      </body>
    </html>
  );
}
