import { describe, it, expect, vi, beforeEach } from "vitest";

/**
 * Goal actions on a patient page.
 *
 * None of these rules are about rendering, so none are asserted through a browser: AAC
 * details never reach a non-AAC goal, a goal that cannot be reached says nothing about
 * whether it exists, and closing a goal is a transition the API refuses twice rather than
 * a delete the UI merely declines to offer.
 */

const api = vi.hoisted(() => {
  class ApiConflictError extends Error {}

  return {
    ApiConflictError,
    create: vi.fn(),
    markMet: vi.fn(),
    discontinue: vi.fn(),
    revalidatePath: vi.fn(),
    /*
     * redirect() throws, and these actions must never call it.
     *
     * Adding a goal leaves the clinician on the patient page — there is nowhere better to
     * send her, and a navigation would lose her place in a list she is working down. It
     * is mocked as a throw anyway so that adding one later cannot pass unnoticed.
     */
    redirect: vi.fn((url: string) => {
      throw new Error(`NEXT_REDIRECT:${url}`);
    }),
  };
});

vi.mock("server-only", () => ({}));
vi.mock("next/cache", () => ({ revalidatePath: api.revalidatePath }));
vi.mock("next/navigation", () => ({ redirect: api.redirect }));
vi.mock("@/lib/api/notes", () => ({
  ApiConflictError: api.ApiConflictError,
  goalsApi: { create: api.create, markMet: api.markMet, discontinue: api.discontinue },
}));

import { addGoal, markGoalMet, discontinueGoal } from "./actions";
import { INITIAL_ADD_GOAL_STATE, INITIAL_GOAL_TRANSITION_STATE } from "./state";

const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000a1";
const GOAL = "8f3c1d2e-0000-4000-8000-0000000000b1";

const FIELDS = {
  patientPublicId: PATIENT,
  goalText: "Request a break using a core board.",
  domain: "Aac",
  targetCriteria: "4 of 5 opportunities across 3 sessions",
  cueLevelExpected: "Gestural",
  startDate: "2026-08-24",
  aacModality: "LowTech",
  aacDeviceNotes: "Twelve-cell core board, laminated.",
};

function form(overrides: Partial<Record<string, string>> = {}) {
  const data = new FormData();
  for (const [key, value] of Object.entries({ ...FIELDS, ...overrides })) {
    if (value !== undefined) data.append(key, value);
  }
  return data;
}

function transitionForm(goalPublicId = GOAL, patientPublicId = PATIENT) {
  const data = new FormData();
  if (patientPublicId) data.append("patientPublicId", patientPublicId);
  if (goalPublicId) data.append("goalPublicId", goalPublicId);
  return data;
}

describe("addGoal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.create.mockResolvedValue({ publicId: GOAL });
  });

  it("sends an AAC goal with its AAC details", async () => {
    const result = await addGoal(INITIAL_ADD_GOAL_STATE, form());

    expect(result.status).toBe("added");
    expect(api.create).toHaveBeenCalledWith(PATIENT, {
      goalText: "Request a break using a core board.",
      domain: "Aac",
      startDate: "2026-08-24",
      targetCriteria: "4 of 5 opportunities across 3 sessions",
      cueLevelExpected: "Gestural",
      aacModality: "LowTech",
      aacDeviceNotes: "Twelve-cell core board, laminated.",
    });
  });

  /**
   * The form unmounts these fields on a non-AAC domain, so this can only happen to a
   * hand-crafted post. It still has to fail: the aggregate throws and
   * CK_Goals_AacFieldsOnlyOnAacGoals refuses the row, and a BFF that let the combination
   * through would turn a readable refusal into a 400 from a serialiser.
   */
  it("refuses AAC details on a goal that is not AAC, without calling the API", async () => {
    const result = await addGoal(
      INITIAL_ADD_GOAL_STATE,
      form({ domain: "Articulation", aacModality: "HighTech" }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.aacModality).toBeTruthy();
    expect(api.create).not.toHaveBeenCalled();
  });

  it("sends nulls rather than empty strings for the fields left blank", async () => {
    await addGoal(
      INITIAL_ADD_GOAL_STATE,
      form({ domain: "Fluency", targetCriteria: "", cueLevelExpected: "", aacModality: "", aacDeviceNotes: "" }),
    );

    expect(api.create).toHaveBeenCalledWith(
      PATIENT,
      expect.objectContaining({
        targetCriteria: null,
        cueLevelExpected: null,
        aacModality: null,
        aacDeviceNotes: null,
      }),
    );
  });

  it("echoes what was typed back when validation fails", async () => {
    const result = await addGoal(INITIAL_ADD_GOAL_STATE, form({ goalText: "   " }));

    expect(result.status).toBe("error");
    expect(result.errors.goalText).toBeTruthy();
    expect(result.values?.targetCriteria).toBe(FIELDS.targetCriteria);
    expect(result.values?.aacDeviceNotes).toBe(FIELDS.aacDeviceNotes);
  });

  /**
   * React 19 resets an uncontrolled form after an action. The form remounts on a change of
   * `values` identity, so a successful add MUST NOT echo — otherwise the fields refill
   * with the goal that was just saved and the next one starts as a near-duplicate.
   */
  it("does not echo anything back after a successful add", async () => {
    const result = await addGoal(INITIAL_ADD_GOAL_STATE, form());

    expect(result.status).toBe("added");
    expect(result.values).toBeUndefined();
    expect(result.errors).toEqual({});
  });

  it("refreshes the patient page so the new goal appears", async () => {
    await addGoal(INITIAL_ADD_GOAL_STATE, form());
    expect(api.revalidatePath).toHaveBeenCalledWith(`/patients/${PATIENT}`);
  });

  it("stays on the patient page rather than navigating", async () => {
    await addGoal(INITIAL_ADD_GOAL_STATE, form());
    expect(api.redirect).not.toHaveBeenCalled();
  });

  /**
   * A patient belonging to another provider and a patient that does not exist both arrive
   * as null, because the API answers 404 to each (D052). The action must not invent a
   * distinction the API deliberately refused to make.
   */
  it("says nothing about whether an unreachable patient exists", async () => {
    api.create.mockResolvedValue(null);

    const result = await addGoal(INITIAL_ADD_GOAL_STATE, form());

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
    expect(api.revalidatePath).not.toHaveBeenCalled();
  });

  it("does not call the API without a patient", async () => {
    const result = await addGoal(INITIAL_ADD_GOAL_STATE, form({ patientPublicId: "" }));

    expect(result.status).toBe("error");
    expect(api.create).not.toHaveBeenCalled();
  });

  /** An upstream failure must reach the clinician as a sentence, not as a stack trace. */
  it("does not surface a raw transport error", async () => {
    api.create.mockRejectedValue(new Error("Notes API /patients/x/goals failed with 503"));

    const result = await addGoal(INITIAL_ADD_GOAL_STATE, form());

    expect(result.status).toBe("error");
    expect(result.message).toBeTruthy();
    expect(result.message).not.toMatch(/503|Notes API/);
  });

  /** Nothing here may be logged: a goal is a clinical statement about a child. */
  it("logs nothing", async () => {
    const spies = [
      vi.spyOn(console, "log").mockImplementation(() => {}),
      vi.spyOn(console, "info").mockImplementation(() => {}),
      vi.spyOn(console, "warn").mockImplementation(() => {}),
      vi.spyOn(console, "error").mockImplementation(() => {}),
    ];

    api.create.mockRejectedValue(new Error("boom"));
    await addGoal(INITIAL_ADD_GOAL_STATE, form());

    for (const spy of spies) expect(spy).not.toHaveBeenCalled();
    for (const spy of spies) spy.mockRestore();
  });
});

describe("closing a goal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.markMet.mockResolvedValue({ publicId: GOAL, status: "Met" });
    api.discontinue.mockResolvedValue({ publicId: GOAL, status: "Discontinued" });
  });

  it("marks a goal met and refreshes the page", async () => {
    const result = await markGoalMet(INITIAL_GOAL_TRANSITION_STATE, transitionForm());

    expect(api.markMet).toHaveBeenCalledWith(PATIENT, GOAL);
    expect(result.status).toBe("idle");
    expect(api.revalidatePath).toHaveBeenCalledWith(`/patients/${PATIENT}`);
  });

  it("discontinues a goal and refreshes the page", async () => {
    const result = await discontinueGoal(INITIAL_GOAL_TRANSITION_STATE, transitionForm());

    expect(api.discontinue).toHaveBeenCalledWith(PATIENT, GOAL);
    expect(result.status).toBe("idle");
    expect(api.revalidatePath).toHaveBeenCalledWith(`/patients/${PATIENT}`);
  });

  /**
   * These are state transitions, not deletions. Nothing in the BFF may reach for a DELETE
   * — a met goal is the record of what therapy accomplished, and progress over time is
   * the thing families and payers ask about.
   */
  it("has no way to delete a goal", async () => {
    const notes = await import("@/lib/api/notes");

    expect(Object.keys(notes.goalsApi)).not.toContain("delete");
    expect(Object.keys(notes.goalsApi)).not.toContain("remove");
  });

  /**
   * The aggregate refuses to reopen a closed goal, and the API turns that into a 409 with
   * the domain's own wording. That sentence explains a rule the clinician needs, so it is
   * surfaced rather than flattened into "something went wrong".
   */
  it("surfaces the API's own wording when a goal is already closed", async () => {
    api.markMet.mockRejectedValue(
      new api.ApiConflictError("This goal is closed. Reopening would rewrite the record."),
    );

    const result = await markGoalMet(INITIAL_GOAL_TRANSITION_STATE, transitionForm());

    expect(result.status).toBe("error");
    expect(result.message).toMatch(/closed/i);
    expect(api.revalidatePath).not.toHaveBeenCalled();
  });

  /** 404 covers both "not yours" and "not there". The message must cover neither. */
  it("says nothing about whether an unreachable goal exists", async () => {
    api.discontinue.mockResolvedValue(null);

    const result = await discontinueGoal(INITIAL_GOAL_TRANSITION_STATE, transitionForm());

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });

  it("does not call the API without both identifiers", async () => {
    expect((await markGoalMet(INITIAL_GOAL_TRANSITION_STATE, transitionForm(""))).status)
      .toBe("error");
    expect((await discontinueGoal(INITIAL_GOAL_TRANSITION_STATE, transitionForm(GOAL, ""))).status)
      .toBe("error");

    expect(api.markMet).not.toHaveBeenCalled();
    expect(api.discontinue).not.toHaveBeenCalled();
  });

  it("does not surface a raw transport error", async () => {
    api.markMet.mockRejectedValue(new Error("Notes API failed with 500"));

    const result = await markGoalMet(INITIAL_GOAL_TRANSITION_STATE, transitionForm());

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/500|Notes API/);
    expect(result.message).toBeTruthy();
  });
});
