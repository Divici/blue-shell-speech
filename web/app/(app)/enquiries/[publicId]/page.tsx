import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { enquiriesApi } from "@/lib/api/enquiries";
import { formatAgeMonths } from "@/lib/age";
import {
  ENQUIRY_STATUS_LABELS,
  daysWaiting,
  formatSubmittedAt,
  formatWait,
} from "@/lib/enquiry-format";
import { EnquiryActions } from "./EnquiryActions";

export const metadata: Metadata = {
  title: "Enquiry",
  robots: { index: false, follow: false },
};

/**
 * One consultation enquiry.
 *
 * THE PAGE TITLE IS DELIBERATELY GENERIC, the same rule the patient record follows: a
 * browser tab reading "Jordan Reyes" is a family's name on a screen in somebody's living
 * room, and in screen-recording software during a demo.
 *
 * THIS IS THE ONLY SCREEN THAT SHOWS WHAT THE PARENT WROTE, and the endpoint behind it is
 * the one that records the disclosure. That is the point of D065: the audit belongs on the
 * route the product actually calls, not on a sibling that looks like the read endpoint.
 *
 * The API answers 404 both for an enquiry that does not exist and for one belonging to
 * another provider — deliberately indistinguishable — so this renders `notFound()` for
 * either, and the UI cannot leak the difference by showing a different message.
 */
export default async function EnquiryPage(props: PageProps<"/enquiries/[publicId]">) {
  const { publicId } = await props.params;

  const enquiry = await enquiriesApi.get(publicId);

  if (!enquiry) notFound();

  const waited = daysWaiting(enquiry.submittedAtUtc, new Date());

  return (
    <>
      <Link href="/enquiries" className="text-sm font-medium text-blue-deep hover:underline">
        &larr; Enquiries
      </Link>

      <div className="mt-3 flex flex-wrap items-center gap-3">
        <h1 className="font-display text-3xl font-bold text-navy">{enquiry.parentName}</h1>
        <span className="rounded-full bg-ice px-3 py-1 text-xs font-semibold text-navy">
          {ENQUIRY_STATUS_LABELS[enquiry.status] ?? enquiry.status}
        </span>
      </div>

      <p className="mt-1 text-ink-muted">
        {/* Practice-local, always. The API sends UTC and Michelle works in Maryland. */}
        Arrived {formatSubmittedAt(enquiry.submittedAtUtc)}
        <span aria-hidden="true"> · </span>
        waiting {formatWait(waited).toLowerCase()}
      </p>

      <div className="mt-8 grid gap-6 lg:grid-cols-3">
        <section className="rounded-2xl border border-ice bg-white p-6 lg:col-span-2">
          <h2 className="font-display text-xl font-bold text-navy">
            About {enquiry.childFirstName}
          </h2>
          <p className="mt-1 text-sm text-ink-muted">
            {formatAgeMonths(enquiry.childAgeMonths)} old, as the parent gave it.
          </p>

          {/*
            The parent's own words, whitespace preserved. Not summarised and not edited —
            this is the account the practice was given, and a clinician's assessment is a
            different thing that belongs on a patient record.
          */}
          <p className="mt-4 whitespace-pre-wrap leading-relaxed text-ink">
            {enquiry.concerns}
          </p>
        </section>

        <aside className="space-y-6">
          <section className="rounded-2xl border border-ice bg-white p-6">
            <h2 className="font-display text-lg font-bold text-navy">How to reach them</h2>

            <p className="mt-3 text-sm text-ink-muted">
              Prefers {enquiry.preferredContactMethod.toLowerCase()}
            </p>

            {/*
              Tappable, because this page is opened on a phone in order to make the call.
            */}
            <p className="mt-2 break-words text-sm">
              <a
                href={`mailto:${enquiry.email}`}
                className="font-medium text-blue-deep hover:underline"
              >
                {enquiry.email}
              </a>
            </p>

            {enquiry.phone ? (
              <p className="mt-1 text-sm">
                <a
                  href={`tel:${enquiry.phone.replace(/[^0-9+]/g, "")}`}
                  className="font-medium text-blue-deep hover:underline"
                >
                  {enquiry.phone}
                </a>
              </p>
            ) : (
              /*
                Stated rather than left blank. The form only requires a number when the
                parent asked to be phoned, so an absent one is a real answer — and a blank
                space reads as a page that failed to load something.
              */
              <p className="mt-1 text-sm text-ink-muted">No phone number was left.</p>
            )}
          </section>
        </aside>
      </div>

      <div className="mt-6">
        <EnquiryActions enquiry={enquiry} />
      </div>
    </>
  );
}
