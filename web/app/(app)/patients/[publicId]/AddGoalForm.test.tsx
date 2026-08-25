import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

/**
 * Writing a goal.
 *
 * The rule under test is the AAC one. The aggregate throws on AAC details attached to a
 * non-AAC goal and CK_Goals_AacFieldsOnlyOnAacGoals refuses the row — so the form must not
 * offer the combination, and must not merely HIDE the fields while still submitting them.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({ addGoal: vi.fn(), markGoalMet: vi.fn(), discontinueGoal: vi.fn() }));

import { AddGoalForm } from "./AddGoalForm";
import { EMPTY_GOAL_VALUES } from "./state";

const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000a1";
const TODAY = "2026-08-24";

function renderForm() {
  return render(<AddGoalForm patientPublicId={PATIENT} defaultStartDate={TODAY} />);
}

const aacFieldNames = ["aacModality", "aacDeviceNotes"] as const;

function aacFields(container: HTMLElement) {
  return aacFieldNames
    .map((name) => container.querySelector(`[name="${name}"]`))
    .filter((el): el is Element => el !== null);
}

describe("AddGoalForm", () => {
  beforeEach(() => vi.clearAllMocks());

  it("submits the patient it belongs to", () => {
    const { container } = renderForm();
    expect(container.querySelector('input[name="patientPublicId"]')).toHaveValue(PATIENT);
  });

  it("offers every domain the enum defines", () => {
    renderForm();

    const options = screen.getAllByRole("option", { name: /./ });
    const labels = options.map((o) => o.textContent);

    expect(labels).toContain("AAC");
    expect(labels).toContain("Articulation");
    expect(labels).toContain("Social communication");
  });

  it("defaults the start date to the practice's today", () => {
    const { container } = renderForm();
    expect(container.querySelector('input[name="startDate"]')).toHaveValue(TODAY);
  });

  // ----------------------------------------------------------------- AAC

  it("does not render the AAC fields for a non-AAC domain", () => {
    const { container } = renderForm();

    expect(aacFields(container)).toHaveLength(0);
    expect(screen.queryByLabelText(/modality/i)).not.toBeInTheDocument();
  });

  it("reveals the AAC fields when the domain is AAC", async () => {
    const user = userEvent.setup();
    const { container } = renderForm();

    await user.selectOptions(screen.getByLabelText(/domain/i), "Aac");

    expect(aacFields(container)).toHaveLength(aacFieldNames.length);
    expect(screen.getByLabelText(/modality/i)).toBeInTheDocument();
  });

  /**
   * UNMOUNTED, not hidden.
   *
   * A hidden input still posts its value, so hiding would send AAC details on an
   * articulation goal — rejected by the aggregate and by the CHECK, and rejected by the
   * server action too, but only after the clinician has been shown an error for something
   * she cannot see. Removing the fields is the only version where the form and the
   * database agree.
   */
  it("removes the AAC fields again when the domain changes away from AAC", async () => {
    const user = userEvent.setup();
    const { container } = renderForm();
    const domain = screen.getByLabelText(/domain/i);

    await user.selectOptions(domain, "Aac");
    await user.type(screen.getByLabelText(/device/i), "Core board on the family tablet.");

    await user.selectOptions(domain, "Articulation");

    expect(aacFields(container)).toHaveLength(0);
    expect(container.innerHTML).not.toContain("Core board on the family tablet.");
  });

  it("offers every AAC modality the enum defines", async () => {
    const user = userEvent.setup();
    renderForm();

    await user.selectOptions(screen.getByLabelText(/domain/i), "Aac");

    const modality = screen.getByLabelText(/modality/i);
    expect(modality).toHaveTextContent("PECS");
    expect(modality).toHaveTextContent(/high-tech/i);
  });

  // ------------------------------------------------------------- feedback

  it("marks a rejected field invalid and shows its reason", () => {
    render(
      <AddGoalForm
        patientPublicId={PATIENT}
        defaultStartDate={TODAY}
        initialState={{
          status: "error",
          errors: { goalText: "Write the goal." },
          values: { ...EMPTY_GOAL_VALUES, domain: "Articulation", startDate: TODAY },
        }}
      />,
    );

    expect(screen.getByText("Write the goal.")).toBeInTheDocument();
    expect(screen.getByLabelText(/^goal/i)).toHaveAttribute("aria-invalid", "true");
  });

  /**
   * D042 finding #3, in a clinical form: a validation failure that erases what was typed
   * is worse than the failure. These are uncontrolled inputs remounted on echo, because
   * React 19 resets a form after an action and defaultValue alone does not survive it.
   */
  it("puts back what was typed when the goal is rejected", () => {
    render(
      <AddGoalForm
        patientPublicId={PATIENT}
        defaultStartDate={TODAY}
        initialState={{
          status: "error",
          errors: { startDate: "Pick the date this goal starts." },
          values: {
            goalText: "Request a break using a core board.",
            domain: "Aac",
            targetCriteria: "4 of 5 opportunities",
            cueLevelExpected: "Gestural",
            startDate: "",
            aacModality: "LowTech",
            aacDeviceNotes: "Twelve-cell core board.",
          },
        }}
      />,
    );

    expect(screen.getByLabelText(/^goal/i)).toHaveValue("Request a break using a core board.");
    expect(screen.getByLabelText(/criteria/i)).toHaveValue("4 of 5 opportunities");

    // Including the AAC fields, which must come back visible because the echoed domain is AAC.
    expect(screen.getByLabelText(/device/i)).toHaveValue("Twelve-cell core board.");
  });

  it("shows a whole-form failure as an alert", () => {
    render(
      <AddGoalForm
        patientPublicId={PATIENT}
        defaultStartDate={TODAY}
        initialState={{
          status: "error",
          errors: {},
          message: "We could not save this goal. Please try again.",
        }}
      />,
    );

    expect(screen.getByRole("alert")).toHaveTextContent(/could not save this goal/i);
  });

  /**
   * A goal is a clinical statement about a child. Nothing about it may be attributed to a
   * failure the clinician cannot act on, and nothing may name the API.
   */
  it("never names the API in what it shows", () => {
    const { container } = render(
      <AddGoalForm
        patientPublicId={PATIENT}
        defaultStartDate={TODAY}
        initialState={{
          status: "error",
          errors: {},
          message: "We could not save this goal. Please try again.",
        }}
      />,
    );

    expect(container.textContent).not.toMatch(/api|404|409|fetch/i);
  });
});
