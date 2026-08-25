import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";

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
