import Link from "next/link";
import { CONTACT } from "@/lib/site-content";
import { practiceContact } from "@/lib/practice-contact";
import { PhoneIcon, MailIcon, HomeIcon, ArrowRightIcon } from "@/components/icons";

/**
 * "Get In Touch" — added at Michelle's request when the testimonials section was cut.
 *
 * Contact details come from environment configuration, never from the repository
 * (CLAUDE.md non-negotiable #7 — this repo is public). In development they fall back to
 * obvious placeholders in the 555-01xx range reserved for fiction, so a stand-in that
 * escapes into a screenshot cannot ring a real person.
 *
 * No street address is published. In-home therapy travels to the patient, and Michelle's
 * home address must never appear anywhere.
 */
export function GetInTouch() {
  const contact = practiceContact();

  return (
    <section id="contact" className="bg-white pb-[var(--spacing-section)]">
      <div className="mx-auto max-w-4xl px-4 text-center sm:px-6">
        <p className="text-sm font-semibold uppercase tracking-[0.18em] text-blue-deep">
          {CONTACT.eyebrow}
        </p>
        <h2 className="mt-2 font-display text-[clamp(1.9rem,4vw,2.75rem)] font-bold text-navy">
          {CONTACT.heading}
        </h2>
        <p className="mx-auto mt-4 max-w-2xl text-lg leading-relaxed text-ink-muted">
          {CONTACT.body}
        </p>

        <div className="mt-9 flex flex-wrap justify-center gap-3">
          <Link
            href="/consultation"
            className="inline-flex items-center gap-2 rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90"
          >
            Request a Free Consultation
            <ArrowRightIcon size={18} />
          </Link>
        </div>

        <dl className="mx-auto mt-12 grid max-w-2xl gap-6 text-left sm:grid-cols-3">
          <div className="flex items-start gap-3">
            <PhoneIcon size={20} className="mt-1 shrink-0 text-teal" />
            <div>
              <dt className="text-sm font-semibold text-navy">Phone</dt>
              <dd className="text-sm text-ink-muted">
                <a href={`tel:${contact.phone.replace(/[^0-9+]/g, "")}`} className="hover:text-blue-deep">
                  {contact.phone}
                </a>
              </dd>
            </div>
          </div>

          <div className="flex items-start gap-3">
            <MailIcon size={20} className="mt-1 shrink-0 text-teal" />
            <div>
              <dt className="text-sm font-semibold text-navy">Email</dt>
              <dd className="text-sm break-words text-ink-muted">
                <a href={`mailto:${contact.email}`} className="hover:text-blue-deep">
                  {contact.email}
                </a>
              </dd>
            </div>
          </div>

          <div className="flex items-start gap-3">
            <HomeIcon size={20} className="mt-1 shrink-0 text-teal" />
            <div>
              <dt className="text-sm font-semibold text-navy">Service area</dt>
              <dd className="text-sm text-ink-muted">{CONTACT.serviceArea}</dd>
            </div>
          </div>
        </dl>

        {contact.isPlaceholder && (
          /*
            Visible only in development — resolvePracticeContact throws in production
            rather than rendering a placeholder, so this can never reach a real visitor.
          */
          <p className="mt-8 rounded-xl bg-sand/40 px-4 py-3 text-sm text-navy">
            <strong>Development note:</strong> contact details are placeholders. Set
            NEXT_PUBLIC_PRACTICE_PHONE and NEXT_PUBLIC_PRACTICE_EMAIL before launch.
          </p>
        )}
      </div>
    </section>
  );
}
