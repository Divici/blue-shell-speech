import { describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";

/**
 * The guardians on a patient record.
 *
 * Everything here is about ONE distinction: who Michelle calls, and who may receive the
 * child's file. They are different facts about a family — a stepparent can be the adult
 * who brings the child every week and hold no authority to consent, and a non-custodial
 * parent can hold the authority and never appear at a session — so the page states them
 * separately, states the second in both directions, and never lets one be read off the
 * other.
 *
 * Fixtures are synthetic, Maryland-shaped, and belong to nobody.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({
  addGuardian: vi.fn(),
  updateGuardian: vi.fn(),
  recordAddress: vi.fn(),
  correctAddress: vi.fn(),
}));

import { GuardianSection } from "./GuardianSection";
import type { Guardian } from "@/lib/api/patients";

const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000a1";

const BASE: Guardian = {
  publicId: "8f3c1d2e-0000-4000-8000-0000000000c1",
  firstName: "Jordan",
  lastName: "Reyes",
  relationship: "Mother",
  phone: "410-555-0142",
  email: null,
  isPrimaryContact: true,
  hasLegalAuthority: true,
};

function guardian(overrides: Partial<Guardian> = {}): Guardian {
  return { ...BASE, ...overrides };
}

function renderSection(guardians: Guardian[]) {
  return render(<GuardianSection patientPublicId={PATIENT} guardians={guardians} />);
}

/**
 * The guardian list only.
 *
 * Every card sits inside the <ul>, and the add form below carries the same words on its
 * labels and radios. A query that cannot tell "May receive records, the fact" from "may
 * receive records, the choice" is not asserting what it claims to — the same trap
 * GoalList.test.tsx records about domain badges.
 */
function cards() {
  return within(screen.getByRole("list", { name: /guardians/i }));
}

/**
 * The badges are matched EXACTLY, not by pattern.
 *
 * jsdom exposes the contents of a closed <details> to Testing Library (the behaviour D063
 * records), so each card carries its own edit form and every one of that form's labels.
 * A loose /may receive records/i would match the radio reading "Yes — they may receive
 * records" as readily as the badge stating the fact — which is the difference between
 * asserting what the record SAYS and asserting that a control exists to change it.
 */
const MAY_RECEIVE = "May receive records";
const NO_ACCESS = "No records access";
const PRIMARY = "Primary contact";

describe("GuardianSection", () => {
  it("says so when nobody has been recorded yet", () => {
    renderSection([]);

    expect(screen.getByText(/no guardians recorded yet/i)).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: /guardians/i })).not.toBeInTheDocument();
  });

  /**
   * An empty record is not the same as a record where the question was asked and the
   * answer was no. The banner belongs to the second, which is the one that looks fine
   * until somebody requests the file.
   */
  it("does not warn about records access when there are no guardians at all", () => {
    renderSection([]);

    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  /**
   * THE STATE THIS SECTION EXISTS TO MAKE VISIBLE.
   *
   * Two guardians, both entered, neither authorised. Nothing is broken and nothing is
   * missing — a family whose custody paperwork has not arrived genuinely has nobody
   * entitled to the file yet. The page says so instead of picking somebody.
   *
   * Control: GuardianSection — the `release === "none-authorised"` block.
   * Deleted → red on the first assertion, "TestingLibraryElementError: Unable to find an
   * accessible element with the role "status"".
   */
  it("says plainly when nobody on the record may receive the child's records", () => {
    renderSection([
      guardian({ hasLegalAuthority: false }),
      guardian({
        publicId: "8f3c1d2e-0000-4000-8000-0000000000c2",
        firstName: "Sam",
        relationship: "Father",
        isPrimaryContact: false,
        hasLegalAuthority: false,
      }),
    ]);

    const notice = screen.getByRole("status");

    expect(notice).toHaveTextContent(/no one on this record may receive/i);
    expect(notice).toHaveTextContent(/not implied by being the primary contact/i);
  });

  /**
   * Control: recordsReleaseState — the `.some(g => g.hasLegalAuthority)` test, reached
   * through this component. Replaced with `.every(...)` → red,
   * "expect(element).not.toBeInTheDocument()": the banner appears on a record that does
   * have an authorised guardian.
   */
  it("carries no banner once someone is authorised", () => {
    renderSection([
      guardian({ hasLegalAuthority: true }),
      guardian({
        publicId: "8f3c1d2e-0000-4000-8000-0000000000c2",
        firstName: "Sam",
        isPrimaryContact: false,
        hasLegalAuthority: false,
      }),
    ]);

    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  /**
   * STATED IN BOTH DIRECTIONS, on every card.
   *
   * An absent badge would mean "no authority" and "we never recorded it" equally, and the
   * reader cannot tell which. On the question of who may hold a child's medical file, an
   * unlabelled card is worse than a plain no.
   *
   * Control: GuardianSection — the else branch rendering "No records access".
   * Deleted → red, "TestingLibraryElementError: Unable to find an element with the text:
   * No records access" — the card fell silent on the question instead of answering it.
   */
  it("states records access on a guardian who does not have it", () => {
    renderSection([guardian({ hasLegalAuthority: false })]);

    expect(cards().getByText(NO_ACCESS)).toBeInTheDocument();
    expect(cards().queryByText(MAY_RECEIVE)).not.toBeInTheDocument();
  });

  it("states records access on a guardian who does have it", () => {
    renderSection([guardian({ hasLegalAuthority: true })]);

    expect(cards().getByText(MAY_RECEIVE)).toBeInTheDocument();
    expect(cards().queryByText(NO_ACCESS)).not.toBeInTheDocument();
  });

  /**
   * The two badges on one card, disagreeing — which is the whole point of the pair. The
   * primary contact holds no records access; the parent who does is not the contact.
   *
   * Control: GuardianSection — the `guardian.hasLegalAuthority` condition on the badge.
   * Replaced with `guardian.isPrimaryContact` → red, "TestingLibraryElementError: Unable
   * to find an element with the text: No records access": the stepparent's card now reads
   * "May receive records" because she is the one who brings him.
   */
  it("shows the primary contact and the records-access answer as independent facts", () => {
    renderSection([
      guardian({
        firstName: "Alex",
        relationship: "Stepmother",
        isPrimaryContact: true,
        hasLegalAuthority: false,
      }),
      guardian({
        publicId: "8f3c1d2e-0000-4000-8000-0000000000c2",
        firstName: "Sam",
        relationship: "Father",
        isPrimaryContact: false,
        hasLegalAuthority: true,
      }),
    ]);

    const [stepparent, father] = cards().getAllByRole("listitem");

    expect(within(stepparent!).getByText(PRIMARY)).toBeInTheDocument();
    expect(within(stepparent!).getByText(NO_ACCESS)).toBeInTheDocument();

    expect(within(father!).queryByText(PRIMARY)).not.toBeInTheDocument();
    expect(within(father!).getByText(MAY_RECEIVE)).toBeInTheDocument();
  });

  /**
   * Control: GuardianSection — the sort putting the primary contact first.
   * Deleted → red, "expect(element).toHaveTextContent()".
   */
  it("puts the primary contact first, whatever order the record holds them in", () => {
    renderSection([
      guardian({
        publicId: "8f3c1d2e-0000-4000-8000-0000000000c2",
        firstName: "Sam",
        isPrimaryContact: false,
        hasLegalAuthority: false,
      }),
      guardian({ firstName: "Jordan", isPrimaryContact: true }),
    ]);

    const [first] = cards().getAllByRole("listitem");

    expect(first).toHaveTextContent(/Jordan/);
  });

  it("says so when a guardian has no way to be contacted", () => {
    renderSection([
      guardian({ isPrimaryContact: false, phone: null, email: null, hasLegalAuthority: true }),
    ]);

    expect(cards().getByText(/no contact details on file/i)).toBeInTheDocument();
  });

  it("offers an edit form per guardian and one form for adding another", () => {
    renderSection([guardian(), guardian({
      publicId: "8f3c1d2e-0000-4000-8000-0000000000c2",
      firstName: "Sam",
      isPrimaryContact: false,
      hasLegalAuthority: false,
    })]);

    expect(cards().getByText(/edit jordan reyes/i)).toBeInTheDocument();
    expect(cards().getByText(/edit sam reyes/i)).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /add a guardian/i })).toBeInTheDocument();
  });
});
