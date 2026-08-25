import { describe, it, expect, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";

/**
 * The goal list on a patient record.
 *
 * Two rules carry the weight here. AAC details render only on an AAC goal — the same
 * question the aggregate and CK_Goals_AacFieldsOnlyOnAacGoals ask, asked the same way.
 * And closing a goal is a TRANSITION: a met or discontinued goal stays on the page,
 * because it is the record of what therapy accomplished.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({ addGoal: vi.fn(), markGoalMet: vi.fn(), discontinueGoal: vi.fn() }));

import { GoalList } from "./GoalList";
import type { Goal } from "@/lib/api/notes";

const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000a1";


// Synthetic, like every fixture in this repo.
const BASE: Goal = {
  publicId: "8f3c1d2e-0000-4000-8000-0000000000b1",
  goalText: "Produce /s/ in the initial position of words.",
  domain: "Articulation",
  targetCriteria: "80% accuracy over 3 consecutive sessions",
  cueLevelExpected: "Verbal",
  status: "Active",
  startDate: "2026-06-01",
  endDate: null,
  aacModality: null,
  aacDeviceNotes: null,
};

function goal(overrides: Partial<Goal> = {}): Goal {
  return { ...BASE, ...overrides };
}

/**
 * Rendered without the add-goal form on purpose: that form's <option> elements carry the
 * same words as the cards' domain badges, and a test that cannot tell "Articulation the
 * choice" from "Articulation the fact" is not asserting what it claims to.
 */
function renderList(goals: Goal[]) {
  return render(<GoalList patientPublicId={PATIENT} goals={goals} />);
}

describe("GoalList", () => {
  it("says so when there are no goals yet", () => {
    renderList([]);
    expect(screen.getByText(/no goals/i)).toBeInTheDocument();
  });

  it("lists a goal with what it targets and how it is measured", () => {
    renderList([goal()]);

    expect(screen.getByText(BASE.goalText)).toBeInTheDocument();
    expect(screen.getByText("Articulation")).toBeInTheDocument();
    expect(screen.getByText(/80% accuracy over 3 consecutive sessions/)).toBeInTheDocument();
    expect(screen.getByText(/verbal cues/i)).toBeInTheDocument();
  });

  it("uses the term, not the enum member, for AAC", () => {
    renderList([goal({ domain: "Aac" })]);

    expect(screen.getByText("AAC")).toBeInTheDocument();
    expect(screen.queryByText("Aac")).not.toBeInTheDocument();
  });

  // ----------------------------------------------------------------- AAC

  it("shows the AAC details on an AAC goal", () => {
    renderList([
      goal({
        domain: "Aac",
        aacModality: "LowTech",
        aacDeviceNotes: "Twelve-cell core board, laminated.",
      }),
    ]);

    expect(screen.getByText(/low-tech/i)).toBeInTheDocument();
    expect(screen.getByText("Twelve-cell core board, laminated.")).toBeInTheDocument();
  });

  /**
   * Keyed off the DOMAIN, not off whether a value happens to be present.
   *
   * The CHECK makes this row impossible, which is exactly why the assertion is worth
   * making: a card that rendered "whatever is in the AAC columns" would be agreeing with
   * the data instead of with the rule, and would quietly start displaying AAC details on
   * an articulation goal the day something wrote them.
   */
  it("does not show AAC details on a goal whose domain is not AAC", () => {
    renderList([
      goal({
        domain: "Articulation",
        aacModality: "HighTech",
        aacDeviceNotes: "Should never be displayed here.",
      }),
    ]);

    expect(screen.queryByText(/high-tech/i)).not.toBeInTheDocument();
    expect(screen.queryByText("Should never be displayed here.")).not.toBeInTheDocument();
  });

  // ---------------------------------------------------------- transitions

  it("offers both ways to close an active goal", () => {
    renderList([goal()]);

    expect(screen.getByRole("button", { name: /mark met/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /discontinue/i })).toBeInTheDocument();
  });

  it("submits both identifiers with a transition", () => {
    const { container } = renderList([goal()]);

    const patientFields = container.querySelectorAll('input[name="patientPublicId"]');
    const goalFields = container.querySelectorAll('input[name="goalPublicId"]');

    expect(goalFields).toHaveLength(2);
    expect(patientFields).toHaveLength(2);
    for (const field of goalFields) expect(field).toHaveValue(BASE.publicId);
    // One per transition form.
    for (const field of patientFields) expect(field).toHaveValue(PATIENT);
  });

  /**
   * Nothing is deleted, so nothing disappears. "What has this child achieved" is the
   * question families and payers ask, and it is unanswerable from a list that drops a goal
   * the moment it is met.
   */
  it("keeps a met goal on the page, with the date it was met", () => {
    renderList([goal({ status: "Met", endDate: "2026-08-20" })]);

    expect(screen.getByText(BASE.goalText)).toBeInTheDocument();
    expect(screen.getByText("Met")).toBeInTheDocument();
    expect(screen.getByText(/20 August 2026|August 20, 2026/)).toBeInTheDocument();
  });

  it("keeps a discontinued goal on the page", () => {
    renderList([goal({ status: "Discontinued", endDate: "2026-08-20" })]);

    expect(screen.getByText(BASE.goalText)).toBeInTheDocument();
    expect(screen.getByText("Discontinued")).toBeInTheDocument();
  });

  /** The aggregate refuses to reopen a closed goal, so the UI must not offer to. */
  it("offers no transition on a goal that is already closed", () => {
    renderList([goal({ status: "Met", endDate: "2026-08-20" })]);

    expect(screen.queryByRole("button", { name: /mark met/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /discontinue/i })).not.toBeInTheDocument();
  });

  it("separates the goals being worked on from the closed ones", () => {
    renderList([
      goal({ publicId: "a", goalText: "Still working on this." }),
      goal({ publicId: "b", goalText: "Finished this one.", status: "Met", endDate: "2026-08-20" }),
    ]);

    const current = screen.getByRole("list", { name: /current goals/i });
    const closed = screen.getByRole("list", { name: /closed goals/i });

    expect(within(current).getByText("Still working on this.")).toBeInTheDocument();
    expect(within(closed).getByText("Finished this one.")).toBeInTheDocument();
  });

  /** A paused goal is still on the plan, so it stays with the current ones. */
  it("keeps an on-hold goal among the current ones", () => {
    renderList([goal({ status: "OnHold" })]);

    const current = screen.getByRole("list", { name: /current goals/i });
    expect(within(current).getByText(BASE.goalText)).toBeInTheDocument();
    expect(screen.getByText("On hold")).toBeInTheDocument();
  });

  // ------------------------------------------------------- accessible names

  /**
   * Every card carries the same two words, so the visible label alone leaves a screen
   * reader announcing a column of identical buttons. The goal itself disambiguates them.
   */
  it("names the goal in each transition's accessible label", () => {
    renderList([goal()]);

    const met = screen.getByRole("button", { name: /mark met/i });
    const stop = screen.getByRole("button", { name: /discontinue/i });

    expect(met.getAttribute("aria-label")).toContain("Produce /s/");
    expect(stop.getAttribute("aria-label")).toContain("Produce /s/");
  });

  /**
   * WCAG 2.5.3, Label in Name: a speech-input user says what they can see, so an
   * accessible name that does not start with the visible text leaves "click mark met"
   * doing nothing.
   */
  it("keeps the visible text at the start of the accessible name", () => {
    renderList([goal()]);

    const met = screen.getByRole("button", { name: /mark met/i });
    expect(met.getAttribute("aria-label")).toMatch(/^Mark met/);
    expect(met).toHaveTextContent("Mark met");
  });

  /** A long goal must not become an unreadable announcement. */
  it("does not put a whole thousand-character goal into a label", () => {
    renderList([goal({ goalText: "z".repeat(1000) })]);

    const met = screen.getByRole("button", { name: /mark met/i });
    expect((met.getAttribute("aria-label") ?? "").length).toBeLessThan(120);
  });
});
