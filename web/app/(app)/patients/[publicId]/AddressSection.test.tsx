import { describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";

/**
 * A patient's addresses.
 *
 * The rule under test: A CORRECTION IS NOT A MOVE. Recording a new address closes the
 * current one of that type and keeps it, because a note describing a visit last spring
 * refers to where the family lived then. Correcting one changes a row in place, because
 * the family never lived at the mistyped address.
 *
 * So there are two controls, the correction form has no address type and no dates on it at
 * all, and superseded addresses stay on the page.
 *
 * Every address here is synthetic and Maryland-shaped. Michelle's own address is not in
 * this repo (CLAUDE.md non-negotiable #7).
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({
  recordAddress: vi.fn(),
  correctAddress: vi.fn(),
}));

import { AddressSection } from "./AddressSection";
import type { PatientAddress } from "@/lib/api/patients";

const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000a1";
const TODAY = "2026-08-25";

const BASE: PatientAddress = {
  publicId: "8f3c1d2e-0000-4000-8000-0000000000d1",
  line1: "14 Elm Street",
  line2: null,
  city: "Towson",
  state: "MD",
  postalCode: "21204",
  addressType: "Session",
  notes: "Gate code 4821",
  isCurrent: true,
  effectiveFrom: "2026-03-01",
  effectiveTo: null,
};

function address(overrides: Partial<PatientAddress> = {}): PatientAddress {
  return { ...BASE, ...overrides };
}

function renderSection(addresses: PatientAddress[]) {
  return render(
    <AddressSection
      patientPublicId={PATIENT}
      addresses={addresses}
      defaultEffectiveFrom={TODAY}
    />,
  );
}

/** The current-address cards only — the "record a new address" form repeats their labels. */
function cards() {
  return within(screen.getByRole("list", { name: /^addresses$/i }));
}

describe("AddressSection", () => {
  it("says so when there is no address on file", () => {
    renderSection([]);

    expect(screen.getByText(/no address on file/i)).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: /^addresses$/i })).not.toBeInTheDocument();
  });

  it("shows the current session address with how to get in", () => {
    renderSection([address()]);

    const card = cards().getByRole("listitem");

    expect(within(card).getByText(/14 Elm Street/)).toBeInTheDocument();
    expect(within(card).getByText(/Towson, MD 21204/)).toBeInTheDocument();
    expect(within(card).getByText(/gate code 4821/i)).toBeInTheDocument();
    expect(within(card).getByText(/in use since march 1, 2026/i)).toBeInTheDocument();
  });

  /**
   * The session address is the one read on the way to a visit, so it comes first whatever
   * order the record holds them in.
   *
   * Control: AddressSection — the sort putting Session before Billing.
   * Deleted → red, "expect(element).toHaveTextContent()".
   */
  it("puts the session address before the billing address", () => {
    renderSection([
      address({
        publicId: "8f3c1d2e-0000-4000-8000-0000000000d2",
        addressType: "Billing",
        line1: "PO Box 12",
        notes: null,
      }),
      address(),
    ]);

    const [first] = cards().getAllByRole("listitem");

    expect(first).toHaveTextContent(/session address/i);
  });

  /**
   * SUPERSEDED ADDRESSES STAY ON THE PAGE.
   *
   * They are the reason a move supersedes rather than overwrites: a note or a superbill
   * from that period refers to them. Hiding them would make the versioning invisible and
   * make "correct this address" look like the way to record a move.
   *
   * Control: AddressSection — the `previous.length > 0` block.
   * Deleted → red, "TestingLibraryElementError: Unable to find an accessible element with
   * the role "list" and name /previously/i".
   */
  it("keeps a superseded address visible, with the dates it applied", () => {
    renderSection([
      address(),
      address({
        publicId: "8f3c1d2e-0000-4000-8000-0000000000d3",
        line1: "8 Oak Lane",
        notes: null,
        isCurrent: false,
        effectiveFrom: "2024-06-01",
        effectiveTo: "2026-03-01",
      }),
    ]);

    const history = within(screen.getByRole("list", { name: /previously/i }));

    expect(history.getByText(/8 Oak Lane/)).toBeInTheDocument();
    expect(history.getByText(/June 1, 2024 to March 1, 2026/)).toBeInTheDocument();
    // …and it is not offered as a current address.
    expect(cards().queryByText(/8 Oak Lane/)).not.toBeInTheDocument();
  });

  /**
   * THE TWO OPERATIONS ARE TWO CONTROLS, and the page says which is which. A single "edit
   * address" would have to guess, and would be wrong either way: used for a typo it invents
   * a move, used for a move it erases where past visits happened.
   */
  it("offers recording a move and correcting a typo as different things", () => {
    renderSection([address()]);

    expect(cards().getByText(/correct this address/i)).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: /record a new address/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/use this when the family has moved/i)).toBeInTheDocument();
    expect(screen.getByText(/the old one stays on the record/i)).toBeInTheDocument();
  });

  /**
   * THE CORRECTION FORM CANNOT MOVE THE TYPE OR THE DATE, because neither field is on it.
   *
   * The absence is the guard at every layer: not rendered here, not read by the action, and
   * absent from CorrectAddressRequest. A correction that could change them would rewrite
   * where a past visit happened, or leave two current session addresses.
   *
   * Control: CorrectAddressForm — the absence of an address-type select and an
   * effective-date input. Given them, copied from RecordAddressForm → red,
   * "AssertionError: expected <select …(2)>…(2)</select> to be null".
   *
   * NOTE ON <details>: jsdom exposes the contents of a closed disclosure to Testing
   * Library, so the correction form is queryable here even though a browser has it shut.
   * That is why both queries are scoped INSIDE the card — the "record a new address" form
   * below carries exactly the two fields this asserts are missing (D063 records the same
   * jsdom behaviour).
   */
  it("gives the correction form no address type and no effective date", () => {
    renderSection([address()]);

    const card = cards().getByRole("listitem");

    expect(within(card).getByLabelText(/street address/i)).toBeInTheDocument();
    expect(within(card).queryByLabelText(/what is this address for/i)).toBeNull();
    expect(within(card).queryByLabelText(/in use from/i)).toBeNull();
  });

  it("does offer both on the form that records a move", () => {
    renderSection([address()]);

    expect(screen.getByLabelText(/what is this address for/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/in use from/i)).toHaveValue(TODAY);
  });
});
