import type { Metadata } from "next";
import { enquiriesApi } from "@/lib/api/enquiries";
import { PendingLink } from "@/components/loading/PendingLink";
import { EnquiryList } from "./EnquiryList";

export const metadata: Metadata = {
  title: "Enquiries",
  robots: { index: false, follow: false },
};

/**
 * The consultation inbox — where "New consultation request, sign in to view" leads.
 *
 * A Server Component: an enquiry holds a child's first name beside a parent's description
 * of that child's difficulties, so it is rendered on the server and never exists as JSON
 * in a browser bundle or a client-side cache. The route group's layout sets
 * `force-dynamic` and redirects without a session, and the API re-checks ownership on
 * every request regardless — hiding a page is not authorization.
 *
 * READING THIS LIST IS AUDITED on the API side, with a count of how many enquiries came
 * back. That is a property of the endpoint rather than of this page (D065): the audit must
 * not depend on which screen happens to call it.
 */
export default async function EnquiriesPage(props: PageProps<"/enquiries">) {
  const params = await props.searchParams;
  const requested = typeof params.status === "string" ? params.status : "all";

  /*
   * VALIDATED HERE BEFORE IT IS SENT.
   *
   * The API refuses a status outside the vocabulary with a 400, which would surface as an
   * unexplained failure on a screen the clinician reached by editing a URL. Resolving an
   * unknown value to "all" keeps the page useful; the API's refusal is still the control,
   * and it is asserted there.
   */
  const filter = STATUS_FILTERS.some((f) => f.value === requested) ? requested : "all";

  const enquiries = await enquiriesApi.list(filter === "all" ? undefined : filter);

  /*
   * One reading of the clock for the whole page.
   *
   * The rows use it to say how long each family has been waiting, and two rows reading it
   * independently could straddle midnight and disagree about the same enquiry.
   */
  const now = new Date();

  const waiting = enquiries.filter((e) => e.status === "New").length;

  return (
    <>
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-display text-3xl font-bold text-navy">Enquiries</h1>
        {filter === "all" && waiting > 0 && (
          <p className="text-sm text-ink-muted">
            <strong className="text-navy">{waiting}</strong>{" "}
            {waiting === 1 ? "family is" : "families are"} still waiting for a reply.
          </p>
        )}
      </div>

      {/*
        Links rather than a client-side control: each filter is a URL, so a phone's back
        button works, a filtered inbox can be bookmarked, and the page needs no JavaScript
        to change what it shows.

        THE SELECTED TAB IS DERIVED FROM THE SEARCH PARAMETER THIS RENDER IS WAITING ON, so
        for the length of a cold start the previously selected tab is still the highlighted
        one — the clinician has tapped and the strip looks untouched. PendingLink marks the
        one she pressed, without turning any of these into a button and losing the three
        properties above.
      */}
      <nav aria-label="Filter enquiries" className="mt-6 flex flex-wrap gap-2">
        {STATUS_FILTERS.map((option) => {
          const selected = option.value === filter;

          return (
            <PendingLink
              key={option.value}
              href={option.value === "all" ? "/enquiries" : `/enquiries?status=${option.value}`}
              aria-current={selected ? "page" : undefined}
              className={
                selected
                  ? "rounded-full bg-blue-action px-4 py-2 text-sm font-semibold text-white"
                  : "rounded-full border border-ice bg-white px-4 py-2 text-sm font-medium text-ink-muted transition-colors hover:border-blue hover:text-blue-deep"
              }
            >
              {option.label}
            </PendingLink>
          );
        })}
      </nav>

      <EnquiryList enquiries={enquiries} now={now} filter={filter} />
    </>
  );
}

/**
 * The tabs, in the order the API sorts by.
 *
 * The four status names are fixed and never renumbered (ConsultationStatus), so this list
 * can be exhaustive rather than defensive — and any value not on it resolves to "all"
 * above rather than being sent to an endpoint that will refuse it.
 */
const STATUS_FILTERS = [
  { value: "all", label: "All" },
  { value: "New", label: "New" },
  { value: "Contacted", label: "Contacted" },
  { value: "Converted", label: "Became patients" },
  { value: "Declined", label: "Declined" },
] as const;
