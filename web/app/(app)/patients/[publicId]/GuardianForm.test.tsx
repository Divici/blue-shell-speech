import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

/**
 * The guardian form, and one control on it in particular.
 *
 * `HasLegalAuthority` decides who may receive a child's medical file. The column is a bit
 * and has no room for "nobody said", so the FORM is the last layer at which an unanswered
 * question is still distinguishable from a decided one. That is why it is a radio group
 * with nothing preselected rather than a checkbox — a checkbox submits false for "she may
 * not" and for "nobody looked", and those are not the same claim about a family.
 *
 * The other half of the rule is separation: it is never derived from, defaulted from, or
 * grouped with the primary contact.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({
  addGuardian: vi.fn(),
  updateGuardian: vi.fn(),
}));

import { GuardianForm } from "./GuardianForm";
import type { GuardianInput } from "@/lib/guardian-schema";

const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000a1";
const GUARDIAN = "8f3c1d2e-0000-4000-8000-0000000000c1";

const yes = () => screen.getByRole("radio", { name: /yes — they may receive records/i });
const no = () => screen.getByRole("radio", { name: /^no$/i });
const primary = () => screen.getByRole("checkbox", { name: /primary contact/i });

const EXISTING: GuardianInput = {
  firstName: "Jordan",
  lastName: "Reyes",
  relationship: "Mother",
  phone: "410-555-0142",
  email: "jordan.reyes@example.com",
  isPrimaryContact: true,
  hasLegalAuthority: "no",
};

describe("GuardianForm — adding", () => {
  /**
   * NEITHER OPTION IS PRESELECTED.
   *
   * There is no third value in the database, so a default here would resolve "nobody
   * answered" silently and permanently — in the direction that decides whether a parent
   * can obtain their own child's records.
   *
   * Control: GuardianForm — `defaultChecked={values.hasLegalAuthority === option.value}`
   * against EMPTY_GUARDIAN_VALUES, whose answer is the empty string. Replaced with
   * `option.value === "no"` → red, "expect(element).not.toBeChecked()".
   */
  it("preselects neither answer on the records question", () => {
    render(<GuardianForm patientPublicId={PATIENT} idPrefix="new-guardian" />);

    expect(yes()).not.toBeChecked();
    expect(no()).not.toBeChecked();
  });

  /**
   * The question is asked in its own group, in words, with the separation spelled out.
   * A bare checkbox beside "Primary contact" would invite exactly the inference this
   * whole slice exists to prevent.
   *
   * Control: GuardianForm — the <legend> on the legal-authority fieldset.
   * Deleted → red, "TestingLibraryElementError: Unable to find an accessible element with
   * the role "group" and name `/may this person receive/i`" — the fieldset survives, but
   * an unnamed group is not a question anybody is being asked.
   */
  it("asks about records as its own question, and says it is a separate one", () => {
    render(<GuardianForm patientPublicId={PATIENT} idPrefix="new-guardian" />);

    const group = screen.getByRole("group", { name: /may this person receive/i });

    expect(group).toBeInTheDocument();
    expect(group).toHaveTextContent(/separate question from who the primary contact is/i);
  });

  /**
   * Ticking "primary contact" moves nothing on the records question, and the two answers
   * leave the form as two independent fields.
   *
   * The interaction half of this is true by construction — the radios are uncontrolled and
   * nothing is wired to the checkbox — so it is the NAMES that make the test falsifiable,
   * and the click is here to exercise the path a clinician actually takes rather than to
   * carry the assertion. (An earlier draft asserted only the click, which is exactly the
   * shape D070 exists to catch: nothing could have made it fail.)
   *
   * Control: GuardianForm — `name="hasLegalAuthority"` on the radio group.
   * Changed to name="isPrimaryContact" → the two controls collide in one field, and red on
   * the second assertion, "expect(element).toHaveAttribute("name", "hasLegalAuthority")".
   * That collision is the inference made literal: one field, two questions.
   */
  it("keeps the primary contact and the records answer as two separate fields", async () => {
    const user = userEvent.setup();
    render(<GuardianForm patientPublicId={PATIENT} idPrefix="new-guardian" />);

    await user.click(no());
    await user.click(primary());

    expect(primary()).toHaveAttribute("name", "isPrimaryContact");
    expect(no()).toHaveAttribute("name", "hasLegalAuthority");
    expect(no()).toBeChecked();
    expect(yes()).not.toBeChecked();
    expect(primary()).toBeChecked();
  });

  /**
   * Control: GuardianForm — the `{guardianPublicId && …}` condition on the hidden input.
   * Deleted, so it always renders → red, "AssertionError: expected <input type="hidden"
   * value …(1)></input> to be null": the add form would post an empty guardian id.
   */
  it("starts blank and carries the patient identifier", () => {
    const { container } = render(
      <GuardianForm patientPublicId={PATIENT} idPrefix="new-guardian" />,
    );

    expect(screen.getByLabelText(/first name/i)).toHaveValue("");
    expect(screen.getByRole("button", { name: /add guardian/i })).toBeInTheDocument();
    expect(container.querySelector('input[name="patientPublicId"]')).toHaveValue(PATIENT);
    expect(container.querySelector('input[name="guardianPublicId"]')).toBeNull();
  });

  /**
   * Control: GuardianForm — the `state.errors.hasLegalAuthority && <p>` render.
   * Deleted → red, "TestingLibraryElementError: Unable to find an element with the text:
   * /say whether this person may receive/i" — the form comes back refused with nothing on
   * it saying which question went unanswered.
   */
  it("renders a rejection next to the field it belongs to", () => {
    render(
      <GuardianForm
        patientPublicId={PATIENT}
        idPrefix="new-guardian"
        initialState={{
          status: "error",
          errors: { hasLegalAuthority: "Say whether this person may receive the child's records." },
          values: {
            firstName: "Jordan",
            lastName: "Reyes",
            relationship: "Mother",
            phone: "410-555-0142",
            email: "",
            isPrimaryContact: true,
            hasLegalAuthority: "",
          },
        }}
      />,
    );

    expect(screen.getByText(/say whether this person may receive/i)).toBeInTheDocument();
    // …and what was typed is still there.
    expect(screen.getByLabelText(/first name/i)).toHaveValue("Jordan");
  });
});

describe("GuardianForm — editing", () => {
  /**
   * An existing guardian HAS an answer on record, so the radio arrives selected. Only a
   * new guardian starts with nothing chosen; a saved row is never re-asked.
   *
   * The fixture is the exact shape this slice is about: a primary contact whose answer is
   * NO. If anything anywhere derived the records answer from the contact flag, this
   * guardian would be shown as authorised.
   *
   * Control: GuardianForm — `defaultChecked` reading ONLY values.hasLegalAuthority.
   * Given the inference instead — `values.hasLegalAuthority === option.value ||
   * (option.value === "yes" && values.isPrimaryContact)` → red,
   * "expect(element).toBeChecked()": both radios claim to be default-checked, the browser
   * keeps the last, and the guardian's recorded "no" disappears off the form.
   */
  it("shows the answer already on the record", () => {
    render(
      <GuardianForm
        patientPublicId={PATIENT}
        guardianPublicId={GUARDIAN}
        defaults={EXISTING}
        idPrefix={`guardian-${GUARDIAN}`}
      />,
    );

    expect(no()).toBeChecked();
    expect(yes()).not.toBeChecked();
    expect(primary()).toBeChecked();
  });

  /**
   * Control: GuardianForm — `const editing = Boolean(guardianPublicId)`, which picks the
   * action as well as the wording. Pinned to false → red, "TestingLibraryElementError:
   * Unable to find an accessible element with the role "button" and name /save changes/i":
   * editing a guardian would post to addGuardian and create a second one.
   */
  it("carries both identifiers and offers to save rather than to add", () => {
    const { container } = render(
      <GuardianForm
        patientPublicId={PATIENT}
        guardianPublicId={GUARDIAN}
        defaults={EXISTING}
        idPrefix={`guardian-${GUARDIAN}`}
      />,
    );

    expect(container.querySelector('input[name="patientPublicId"]')).toHaveValue(PATIENT);
    expect(container.querySelector('input[name="guardianPublicId"]')).toHaveValue(GUARDIAN);
    expect(screen.getByRole("button", { name: /save changes/i })).toBeInTheDocument();
    expect(screen.getByLabelText(/relationship/i)).toHaveValue("Mother");
  });

  /**
   * Two forms on one page must not share ids, or every label after the first points at the
   * wrong input — which breaks a screen reader and breaks tapping a label on a phone.
   *
   * Control: GuardianForm — the `idPrefix` namespacing in `id()`.
   * Deleted → red, "expect(received).toHaveValue()": both labels resolve to the first
   * form's input, so the edit form's fields are unreachable by their own labels.
   */
  it("namespaces its ids so two forms can sit on one page", () => {
    render(
      <>
        <GuardianForm patientPublicId={PATIENT} idPrefix="new-guardian" />
        <GuardianForm
          patientPublicId={PATIENT}
          guardianPublicId={GUARDIAN}
          defaults={EXISTING}
          idPrefix={`guardian-${GUARDIAN}`}
        />
      </>,
    );

    const [adding, editing] = screen.getAllByLabelText(/first name/i);

    expect(adding).toHaveValue("");
    expect(editing).toHaveValue("Jordan");
    expect(adding!.id).not.toBe(editing!.id);
  });
});
