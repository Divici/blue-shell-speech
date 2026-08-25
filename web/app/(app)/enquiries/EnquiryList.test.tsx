import { describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";

/**
 * The inbox list.
 *
 * What it must get right is triage: who is waiting, how long they have been waiting, and
 * which ones nobody has answered. What it must NOT do is show what the parent wrote — that
 * is the detail read, and the API records that disclosure as one (D065). The summary type
 * has no field for it, so this is asserted here on the shape the page actually renders.
 *
 * SYNTHETIC DATA ONLY.
 */

vi.mock("server-only", () => ({}));

import { EnquiryList } from "./EnquiryList";
import type { EnquirySummary } from "@/lib/api/enquiries";

const BASE: EnquirySummary = {
  publicId: "8f3c1d2e-0000-4000-8000-0000000000a1",
  parentName: "Jordan Reyes",
  childFirstName: "Maya",
  childAgeMonths: 30,
  preferredContactMethod: "Phone",
  status: "New",
  // 10:30 in Maryland. Deliberately an instant whose UTC date and practice date agree, so
  // the wait assertions below are about the list and not about the conversion.
  submittedAtUtc: "2026-08-20T14:30:00Z",
  convertedPatientPublicId: null,
};

function enquiry(overrides: Partial<EnquirySummary> = {}): EnquirySummary {
  return { ...BASE, ...overrides };
}

const NOW = new Date("2026-08-22T14:00:00Z");

function renderList(enquiries: EnquirySummary[], filter = "all") {
  return render(<EnquiryList enquiries={enquiries} now={NOW} filter={filter} />);
}

describe("EnquiryList", () => {
  it("names the parent, the child, and the child's age", () => {
    renderList([enquiry()]);

    const row = screen.getByRole("listitem");
    expect(within(row).getByText(/Jordan Reyes/)).toBeInTheDocument();
    expect(within(row).getByText(/Maya/)).toBeInTheDocument();
    // The unit early-intervention eligibility uses, rendered the same way the caseload
    // renders it — 30 months is 2y 6m on both screens or on neither.
    expect(within(row).getByText(/2y 6m/)).toBeInTheDocument();
  });

  /**
   * How long a family has been waiting is the thing this screen exists to make obvious.
   *
   * Control: the `daysWaiting(...)` call in EnquiryList.
   * Deleted → red, "TestingLibraryElementError: Unable to find an element with the text:
   * /2 days/".
   */
  it("says how long the family has been waiting", () => {
    renderList([enquiry()]);

    expect(screen.getByText(/2 days/)).toBeInTheDocument();
  });

  /**
   * Control: the `href` on the row Link in EnquiryList.
   * Emptied → red, "TestingLibraryElementError: Unable to find an accessible element with
   * the role 'link' and name /Jordan Reyes/" — a row with nothing to open, on the only
   * screen that reaches the enquiry.
   */
  it("opens the enquiry, addressed by its opaque id", () => {
    renderList([enquiry()]);

    expect(screen.getByRole("link", { name: /Jordan Reyes/ })).toHaveAttribute(
      "href",
      `/enquiries/${BASE.publicId}`,
    );
  });

  /**
   * A converted enquiry says so, and says where the child went.
   *
   * Control: the `convertedPatientPublicId` branch in EnquiryList.
   * Deleted → red, "TestingLibraryElementError: Unable to find an accessible element with
   * the role 'link' and name /Open patient record/i" — the enquiry reads "became a
   * patient" with no way to reach the record it became.
   */
  it("links a converted enquiry to the patient record", () => {
    renderList([
      enquiry({
        status: "Converted",
        convertedPatientPublicId: "8f3c1d2e-0000-4000-8000-0000000000b1",
      }),
    ]);

    expect(screen.getByRole("link", { name: /Open patient record/i })).toHaveAttribute(
      "href",
      "/patients/8f3c1d2e-0000-4000-8000-0000000000b1",
    );
  });

  /**
   * Every status renders as words rather than as the enum name.
   *
   * Control: the ENQUIRY_STATUS_LABELS lookup in EnquiryList.
   * Replaced with the raw status → red, "TestingLibraryElementError: Unable to find an
   * element with the text: /Became a patient/" — the badge reads "Converted", which is a
   * database word.
   */
  it.each([
    ["New", /New/],
    ["Contacted", /Contacted/],
    ["Converted", /Became a patient/],
    ["Declined", /Declined/],
  ] as const)("labels a %s enquiry in words", (status, label) => {
    renderList([enquiry({ status, convertedPatientPublicId: null })]);

    expect(screen.getByText(label)).toBeInTheDocument();
  });

  /**
   * Empty states are behaviour, not decoration, and the two empties mean different things.
   *
   * "No enquiries yet" is a quiet practice. "Nothing matches this filter" is a full inbox
   * with the wrong tab selected — and telling a clinician the practice has had no enquiries
   * when she has simply filtered them out is a message that stops her looking.
   *
   * Control: the `filter === "all"` branch in EnquiryList's empty state.
   * Deleted → red, "TestingLibraryElementError: Unable to find an element with the text:
   * /No enquiries have arrived/i".
   */
  it("distinguishes an empty inbox from an empty filter", () => {
    const { unmount } = renderList([], "all");
    expect(screen.getByText(/No enquiries have arrived/i)).toBeInTheDocument();
    unmount();

    renderList([], "New");
    expect(screen.queryByText(/No enquiries have arrived/i)).not.toBeInTheDocument();
    expect(screen.getByText(/Nothing here right now/i)).toBeInTheDocument();
  });

  /**
   * The parent's description of their child is not on this screen, and there is no field
   * on the summary that could carry it. Asserted against the rendered markup rather than
   * against the type, because a page can always render something the type did not promise.
   */
  it("never shows what the parent wrote about their child", () => {
    const { container } = renderList([enquiry()]);

    expect(container.textContent).not.toMatch(/concern/i);
  });
});
