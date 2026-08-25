import Link from "next/link";
import type { EnquirySummary } from "@/lib/api/enquiries";
import { formatAgeMonths } from "@/lib/age";
import {
  ENQUIRY_STATUS_LABELS,
  daysWaiting,
  formatSubmittedAt,
  formatWait,
} from "@/lib/enquiry-format";

/**
 * The consultation inbox.
 *
 * WHAT THIS SCREEN IS FOR. A parent filled in a form on the public site and was told
 * somebody would be in touch; this is the list that makes that true. It is read on a phone
 * between houses, so the three facts a decision needs — who is waiting, how long, and
 * whether anybody has replied — are on the row itself and not behind a tap.
 *
 * WHAT IS DELIBERATELY NOT HERE. The parent's description of their child's difficulties.
 * That is the detail read, and the API records it as a disclosure; a preview of it on this
 * screen would be a second, larger disclosure that nothing counts (D065). The summary type
 * has no field for it, which is the control rather than this comment.
 *
 * ONE READING OF THE CLOCK for the whole list, passed in: two rows working it out
 * independently could straddle midnight and disagree about the same enquiry.
 */
export function EnquiryList({
  enquiries,
  now,
  filter,
}: {
  enquiries: EnquirySummary[];
  now: Date;
  /** The status being shown, or "all". Only used to word the empty state. */
  filter: string;
}) {
  if (enquiries.length === 0) {
    return <EmptyInbox filter={filter} />;
  }

  return (
    <ul className="mt-6 divide-y divide-ice overflow-hidden rounded-2xl border border-ice bg-white">
      {enquiries.map((enquiry) => (
        <EnquiryRow key={enquiry.publicId} enquiry={enquiry} now={now} />
      ))}
    </ul>
  );
}

function EnquiryRow({ enquiry, now }: { enquiry: EnquirySummary; now: Date }) {
  const waited = daysWaiting(enquiry.submittedAtUtc, now);

  return (
    <li>
      <Link
        href={`/enquiries/${enquiry.publicId}`}
        className="flex flex-wrap items-center gap-x-4 gap-y-2 px-5 py-4 transition-colors hover:bg-mist"
      >
        <span className="min-w-0 flex-1">
          <span className="block font-semibold text-navy">{enquiry.parentName}</span>
          <span className="block text-sm text-ink-muted">
            {enquiry.childFirstName}
            <span aria-hidden="true"> · </span>
            {formatAgeMonths(enquiry.childAgeMonths)}
            <span aria-hidden="true"> · </span>
            prefers {enquiry.preferredContactMethod.toLowerCase()}
          </span>
        </span>

        <span className="text-right text-sm">
          {/*
            The wait, in the practice's calendar days. The exact arrival time is a title so
            it is available without spending a line of a phone screen on it.
          */}
          <span
            className="block font-semibold text-ink"
            title={formatSubmittedAt(enquiry.submittedAtUtc)}
          >
            {formatWait(waited)}
          </span>
          <StatusBadge status={enquiry.status} />
        </span>
      </Link>

      {/*
        Outside the row link, because a link inside a link is not valid markup and a
        keyboard user would have no way to reach the inner one.
      */}
      {enquiry.convertedPatientPublicId && (
        <p className="px-5 pb-4 text-sm">
          <Link
            href={`/patients/${enquiry.convertedPatientPublicId}`}
            className="font-medium text-blue-deep hover:underline"
          >
            Open patient record
          </Link>
        </p>
      )}
    </li>
  );
}

/**
 * The status, in words.
 *
 * "Converted" is a database word. What Michelle needs to read is that this family became a
 * patient — see ENQUIRY_STATUS_LABELS.
 */
function StatusBadge({ status }: { status: EnquirySummary["status"] }) {
  const tone =
    status === "New"
      ? "bg-blue/15 text-navy"
      : status === "Contacted"
        ? "bg-sand/40 text-navy"
        : status === "Converted"
          ? "bg-teal/15 text-teal"
          : "bg-ice text-ink-muted";

  return (
    <span
      className={`mt-1 inline-block rounded-full px-2.5 py-1 text-xs font-semibold ${tone}`}
    >
      {ENQUIRY_STATUS_LABELS[status] ?? status}
    </span>
  );
}

/**
 * Two empty states, because they mean different things.
 *
 * An empty inbox is a quiet week. An empty FILTER is a full inbox with the wrong tab
 * selected — and telling a clinician no enquiries have arrived when she has merely
 * filtered them out is a message that stops her looking.
 */
function EmptyInbox({ filter }: { filter: string }) {
  if (filter === "all") {
    return (
      <div className="mt-6 rounded-2xl border border-ice bg-white px-5 py-10 text-center">
        <p className="font-semibold text-navy">No enquiries have arrived yet.</p>
        <p className="mt-1 text-sm text-ink-muted">
          Requests from the practice website land here.
        </p>
      </div>
    );
  }

  return (
    <div className="mt-6 rounded-2xl border border-ice bg-white px-5 py-10 text-center">
      <p className="font-semibold text-navy">Nothing here right now.</p>
      <p className="mt-1 text-sm text-ink-muted">
        No enquiries are{" "}
        {(ENQUIRY_STATUS_LABELS[filter] ?? filter).toLowerCase()}. Choose{" "}
        <span className="font-medium text-navy">All</span> to see the rest.
      </p>
    </div>
  );
}
