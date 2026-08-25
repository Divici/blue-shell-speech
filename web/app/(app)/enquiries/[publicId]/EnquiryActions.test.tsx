import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

/**
 * What an enquiry offers to do next.
 *
 * The rule under test is that the panel offers only the moves the API will accept. Hiding
 * a button is never the control — the endpoint refuses a closed enquiry whatever the
 * browser sends (D052, and RefusalToTransition) — but offering a move that is going to be
 * refused walks a clinician into an error for no reason, which is the copy defect D076
 * records on the other side of the same boundary.
 *
 * SYNTHETIC DATA ONLY.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({
  markContacted: vi.fn(),
  declineEnquiry: vi.fn(),
  convertToPatient: vi.fn(),
}));

import { EnquiryActions } from "./EnquiryActions";
import { markContacted, declineEnquiry, convertToPatient } from "./actions";
import { INITIAL_CONVERT_STATE } from "./state";
import type { EnquiryDetail } from "@/lib/api/enquiries";

const BASE: EnquiryDetail = {
  publicId: "8f3c1d2e-0000-4000-8000-0000000000a1",
  parentName: "Jordan Reyes",
  email: "jordan@example.com",
  phone: "410-555-0142",
  childFirstName: "Maya",
  childAgeMonths: 30,
  concerns: "She has about ten words but is not combining them.",
  preferredContactMethod: "Phone",
  status: "New",
  submittedAtUtc: "2026-08-20T14:30:00Z",
  convertedPatientPublicId: null,
};

function renderActions(overrides: Partial<EnquiryDetail> = {}) {
  return render(<EnquiryActions enquiry={{ ...BASE, ...overrides }} />);
}

describe("EnquiryActions", () => {
  it("offers the three moves on an enquiry nobody has answered", () => {
    renderActions();

    expect(screen.getByRole("button", { name: /Mark contacted/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Decline/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Create patient record/i })).toBeInTheDocument();
  });

  /**
   * The two fields the public form never collected, and only those.
   *
   * A first name input here would be a value the browser chose for a field the enquiry
   * already answers — the API takes the child's first name off the row, and this form
   * cannot override it. The name is shown as text so the clinician can see what she is
   * about to create.
   *
   * Control: the absence of a first-name input in EnquiryActions.
   * Falsified by adding a firstName Field → red, "expect(element).not.toBeInTheDocument()
   * … expected document not to contain element, found <input".
   */
  it("asks for a surname and a date of birth, and for nothing else about the child", () => {
    renderActions();

    expect(screen.getByLabelText(/Last name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/Date of birth/i)).toBeInTheDocument();

    // The child's first name is shown, not asked for.
    expect(screen.getByText(/Maya/)).toBeInTheDocument();
    expect(screen.queryByLabelText(/First name/i)).not.toBeInTheDocument();
  });

  /**
   * A converted enquiry offers nothing to change and says where the child is.
   *
   * Every move is refused on a converted enquiry — including declining — so a button here
   * would only produce a 409 (RefusalToTransition).
   *
   * Control: the `enquiry.status === "Converted"` branch in EnquiryActions.
   * Deleted → red, "expect(element).not.toBeInTheDocument() … expected document not to
   * contain element, found <button" — the panel offers to decline a family already on the
   * caseload.
   */
  it("offers no moves once the enquiry became a patient", () => {
    renderActions({
      status: "Converted",
      convertedPatientPublicId: "8f3c1d2e-0000-4000-8000-0000000000b1",
    });

    expect(screen.queryByRole("button", { name: /Mark contacted/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Decline/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Create patient record/i })).not.toBeInTheDocument();

    expect(screen.getByRole("link", { name: /Open patient record/i })).toHaveAttribute(
      "href",
      "/patients/8f3c1d2e-0000-4000-8000-0000000000b1",
    );
  });

  /**
   * A declined enquiry is closed and stays as it was.
   *
   * THE ASSERTION THAT MATTERS HERE IS THE DECLINE BUTTON, and it is the second version of
   * this test. The first asserted on "Mark contacted" and on the word "kept" — and both
   * are absent-or-present identically when the branch is deleted, because the main panel
   * already hides "Mark contacted" on anything that is not New and already carries the
   * sentence about a declined enquiry being kept. Two controls covering for each other,
   * the D077 shape, found by running the deletion rather than by reading the assertions.
   *
   * Control: the `enquiry.status === "Declined"` branch in EnquiryActions.
   * Deleted → red, "expected document not to contain element, found <button …>Decline" —
   * the panel offers to decline an enquiry that is already declined.
   */
  it("offers no moves on a declined enquiry, and says it is kept", () => {
    renderActions({ status: "Declined" });

    expect(screen.queryByRole("button", { name: /Mark contacted/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Decline/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Create patient record/i })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /Declined/i })).toBeInTheDocument();
    expect(screen.getByText(/kept/i)).toBeInTheDocument();
  });

  /**
   * A contacted enquiry can still be converted or declined, and marking it contacted again
   * is not offered — the aggregate treats a second reply as the same state, so the button
   * would change nothing.
   *
   * Control: the `enquiry.status === "New"` guard on the Mark-contacted form.
   * Deleted → red, "expect(element).not.toBeInTheDocument() … expected document not to
   * contain element, found <button".
   */
  it("keeps the conversion available after a reply", () => {
    renderActions({ status: "Contacted" });

    expect(screen.getByRole("button", { name: /Create patient record/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Decline/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Mark contacted/i })).not.toBeInTheDocument();
  });

  /**
   * Declining is stated in advance rather than confirmed afterwards.
   *
   * The API refuses to reopen a declined enquiry, so the clinician needs to know that
   * BEFORE she taps — which a dialog after the fact does not achieve. Same reasoning as
   * the goal-closing card (D063).
   */
  it("states what declining does before it is tapped", () => {
    renderActions();

    expect(screen.getByText(/cannot be reopened/i)).toBeInTheDocument();
  });

  /**
   * Every form carries the enquiry's id, and the actions read it from nowhere else. Not an
   * authorization decision — the API re-checks ownership on every one of them — just
   * addressing.
   *
   * Control: the hidden publicId input rendered by EnquiryId.
   * Deleted → red, "AssertionError: expected 0 to be greater than 0" — every form posts
   * with no enquiry on it, and the actions refuse before reaching the API.
   */
  it("addresses every move by the enquiry's opaque id", () => {
    const { container } = renderActions();

    const ids = container.querySelectorAll('input[name="publicId"]');
    expect(ids.length).toBeGreaterThan(0);
    for (const input of ids) {
      expect(input.getAttribute("value")).toBe(BASE.publicId);
    }
  });
});

/**
 * What the panel does while a transition is being written.
 *
 * EVERY MOVE ON THIS SCREEN IS ONE-WAY. Declining cannot be reopened, converting creates a
 * patient record, and marking contacted is the transition the aggregate treats as
 * idempotent only because it already happened. A second POST is not a duplicate button
 * press to shrug at: `convertToPatient` twice is two attempts to create a child's chart
 * from one enquiry.
 *
 * The three buttons were already disabled together while any of them was in flight. The
 * conversion button was the exception in the other direction — disabled, but never
 * relabelled, so on the slowest action of the four the screen dimmed and said nothing.
 */
describe("EnquiryActions while a transition is in flight", () => {
  beforeEach(() => vi.clearAllMocks());

  /** Held until released; an unresolved action leaks into the next test (NoteEditor). */
  function holdable() {
    let release: () => void = () => {};
    let first = true;

    const impl = () => {
      if (!first) return Promise.resolve(INITIAL_CONVERT_STATE);
      first = false;
      return new Promise<typeof INITIAL_CONVERT_STATE>((resolve) => {
        release = () => resolve(INITIAL_CONVERT_STATE);
      });
    };

    return { impl, release: () => release() };
  }

  async function settle(held: { release: () => void }, label: RegExp) {
    held.release();
    await waitFor(() => expect(screen.getByRole("button", { name: label })).toBeEnabled());
  }

  /**
   * Control: the `converting ? "Creating record…" : "Create patient record"` expression on
   * the conversion button.
   * Reduced to the plain label → red, "Unable to find an accessible element with the role
   * \"button\" and name `/creating record/i`", plus the two siblings below, which wait on
   * the label coming back.
   */
  it("says that it is creating the record rather than only greying out", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(convertToPatient).mockImplementation(held.impl);

    renderActions();
    await user.type(screen.getByLabelText(/last name/i), "Sinclair");
    await user.type(screen.getByLabelText(/date of birth/i), "2024-02-14");
    await user.click(screen.getByRole("button", { name: /^create patient record$/i }));

    expect(screen.getByRole("button", { name: /creating record/i })).toBeDisabled();
    await settle(held, /^create patient record$/i);
  });

  /**
   * A second conversion is a second attempt to create a chart for the same child.
   *
   * Control: the `disabled={busy}` attribute on the conversion button.
   * Deleted → red in `settle`, "Unable to find role=\"button\" and name `/^create patient
   * record$/i`": the queued second submission re-enters the pending state the instant the
   * first resolves, so the label never comes back. Two more tests in this block go with
   * it, because the attribute is the same one they lean on.
   */
  it("cannot be made to convert the same enquiry twice", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(convertToPatient).mockImplementation(held.impl);

    renderActions();
    await user.type(screen.getByLabelText(/last name/i), "Sinclair");
    await user.type(screen.getByLabelText(/date of birth/i), "2024-02-14");

    const create = screen.getByRole("button", { name: /^create patient record$/i });
    await user.click(create);
    await user.click(create);

    await settle(held, /^create patient record$/i);

    expect(vi.mocked(convertToPatient)).toHaveBeenCalledTimes(1);
  });

  /**
   * THE FOUR CONTROLS ARE ONE DECISION, not four independent ones.
   *
   * Declining an enquiry that is halfway through becoming a patient is the interleaving
   * this panel must not permit: the aggregate would refuse the second transition, but only
   * after the clinician had asked for both.
   *
   * Control: the `busy` flag — `contacting || declining || converting` — on the decline
   * button.
   * Replaced with `declining` alone → red in `settle`, "Unable to find role=\"button\" and
   * name `/^mark contacted$/i`": the decline the panel should have refused went through,
   * and the enquiry is now mid-decline while the reply it was asked for is still in
   * flight.
   */
  it("locks the other moves while one is being written", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(markContacted).mockImplementation(held.impl);

    renderActions();
    await user.click(screen.getByRole("button", { name: /^mark contacted$/i }));
    await user.click(screen.getByRole("button", { name: /^decline$/i }));

    await settle(held, /^mark contacted$/i);

    expect(vi.mocked(declineEnquiry)).not.toHaveBeenCalled();
  });
});
