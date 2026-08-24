import type { Metadata, Viewport } from "next";
import { Playfair_Display, Inter } from "next/font/google";
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
        {children}
      </body>
    </html>
  );
}
