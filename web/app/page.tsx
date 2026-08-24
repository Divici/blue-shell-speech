import { SiteHeader } from "@/components/marketing/SiteHeader";
import { Hero } from "@/components/marketing/Hero";
import { MeetYourSLP } from "@/components/marketing/MeetYourSLP";
import { GettingStarted } from "@/components/marketing/GettingStarted";
import { GetInTouch } from "@/components/marketing/GetInTouch";
import { SiteFooter } from "@/components/marketing/SiteFooter";

/**
 * The homepage, in the order Michelle confirmed:
 *
 *   Header -> Hero -> three badges -> Meet Your SLP (+ service chips)
 *   -> Getting Started is Easy -> Get In Touch -> Footer
 *
 * Deliberately absent, and not to be reintroduced (docs/SITE_CONTENT.md):
 *   - the "Therapy That's Tailored to Your Child" services grid — replaced by chips
 *   - the testimonials carousel — the reviews were fabricated, so it is DELETED,
 *     not deferred. Placeholder reviews for a healthcare practice are a real problem.
 *
 * Every nav item anchors into this page. Only /consultation and /login are routes.
 */
export default function HomePage() {
  return (
    <>
      <SiteHeader />
      <main id="main">
        <Hero />
        <MeetYourSLP />
        <GettingStarted />
        <GetInTouch />
      </main>
      <SiteFooter />
    </>
  );
}
