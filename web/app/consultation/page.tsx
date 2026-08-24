import type { Metadata } from "next";
import { SiteHeader } from "@/components/marketing/SiteHeader";
import { SiteFooter } from "@/components/marketing/SiteFooter";
import { ConsultationForm } from "./ConsultationForm";
import { WaveDivider } from "@/components/brand/WaveDivider";

export const metadata: Metadata = {
  title: "Request a Free Consultation",
  description:
    "Tell us a little about your child and we'll get back to you within two business days. " +
    "In-home speech-language therapy for children birth to 5 years in Maryland.",
};

/**
 * The consultation route.
 *
 * No comp exists for this page — Michelle asked for it as its own route after the design
 * was drawn. It is built in the established design language rather than inventing a new
 * one, and flagged for the visual gauntlet lane.
 *
 * Its own route rather than a homepage section, per Michelle: a parent should be able to
 * be sent this link directly without scrolling past everything else first.
 */
export default function ConsultationPage() {
  return (
    <>
      <SiteHeader />
      <main id="main">
        <section className="bg-mist pt-12 pb-4">
          <div className="mx-auto max-w-3xl px-4 text-center sm:px-6">
            <p className="text-sm font-semibold uppercase tracking-[0.18em] text-blue-deep">
              Free consultation
            </p>
            <h1 className="mt-2 font-display text-[clamp(2rem,4.5vw,3rem)] font-bold leading-tight text-navy">
              Let’s talk about your child
            </h1>
            <p className="mx-auto mt-4 max-w-xl text-lg leading-relaxed text-ink-muted">
              Tell us a little about what you’re noticing. Michelle will get back to you
              within two business days — there’s no cost, and no obligation to book.
            </p>
          </div>
          <WaveDivider fill="#FFFFFF" className="mt-10" />
        </section>

        <section className="bg-white pb-[--spacing-section]">
          <div className="mx-auto max-w-3xl px-4 sm:px-6">
            <ConsultationForm />
          </div>
        </section>
      </main>
      <SiteFooter />
    </>
  );
}
