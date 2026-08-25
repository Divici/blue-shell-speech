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

import {
  addGoal,
  markGoalMet,
  discontinueGoal,
  addGuardian,
  updateGuardian,
  recordAddress,
  correctAddress,
} from "./actions";
import {
  INITIAL_ADD_GOAL_STATE,
  INITIAL_GOAL_TRANSITION_STATE,
  INITIAL_GUARDIAN_STATE,
  INITIAL_ADDRESS_STATE,
  INITIAL_ADDRESS_CORRECTION_STATE,
} from "./state";

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

/**
 * Guardian and address actions on a patient page.
 *
 * The rule under test throughout is that legal authority — who may receive a child's
 * records — is answered explicitly or not recorded at all. It is never read off the
 * primary-contact checkbox, never defaulted, and never quietly turned into "no" because
 * nobody looked at the question.
 *
 * The second theme is that correcting an address is NOT recording a move. A correction
 * carries no address type and no effective date, so it cannot supersede anything.
 *
 * Every fixture is synthetic and Maryland-shaped, and belongs to nobody.
 */

const PATIENT_API = vi.hoisted(() => ({
  addGuardian: vi.fn(),
  updateGuardian: vi.fn(),
  addAddress: vi.fn(),
  correctAddress: vi.fn(),
}));

vi.mock("@/lib/api/patients", () => ({ patientsApi: PATIENT_API }));

const GUARDIAN = "8f3c1d2e-0000-4000-8000-0000000000c1";
const ADDRESS = "8f3c1d2e-0000-4000-8000-0000000000d1";

const GUARDIAN_FIELDS: Record<string, string> = {
  patientPublicId: PATIENT,
  firstName: "Jordan",
  lastName: "Reyes",
  relationship: "Mother",
  phone: "410-555-0142",
  email: "jordan.reyes@example.com",
  isPrimaryContact: "yes",
  hasLegalAuthority: "yes",
};

const ADDRESS_FIELDS: Record<string, string> = {
  patientPublicId: PATIENT,
  line1: "14 Elm Street",
  line2: "",
  city: "Towson",
  state: "MD",
  postalCode: "21204",
  addressType: "Session",
  notes: "Gate code 4821",
  effectiveFrom: "2026-08-25",
};

/**
 * Builds a FormData the way a browser would.
 *
 * An unchecked checkbox and an unselected radio group send NOTHING — they are absent keys,
 * not empty ones — so passing `undefined` here removes the field entirely rather than
 * sending "". That distinction is the whole subject of these tests.
 */
function formOf(
  base: Record<string, string>,
  overrides: Record<string, string | undefined> = {},
) {
  const data = new FormData();
  for (const [key, value] of Object.entries({ ...base, ...overrides })) {
    if (value !== undefined) data.append(key, value);
  }
  return data;
}

describe("addGuardian", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    PATIENT_API.addGuardian.mockResolvedValue({ publicId: PATIENT });
  });

  it("sends a guardian with an explicit answer on legal authority", async () => {
    const result = await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(result.status).toBe("saved");
    expect(PATIENT_API.addGuardian).toHaveBeenCalledWith(PATIENT, {
      firstName: "Jordan",
      lastName: "Reyes",
      relationship: "Mother",
      phone: "410-555-0142",
      email: "jordan.reyes@example.com",
      isPrimaryContact: true,
      hasLegalAuthority: true,
    });
  });

  /**
   * THE RULE THIS SLICE EXISTS FOR.
   *
   * A radio group with nothing selected sends no field at all. That must reach the
   * clinician as a question, not the database as a "no" — the column cannot hold "nobody
   * said", so the only place the difference survives is before the call.
   *
   * Control: addGuardian — the errors gate before patientsApi.addGuardian, including its
   * `value.hasLegalAuthority === undefined` clause. Deleted → red on the first assertion,
   * "AssertionError: expected 'saved' to be 'error'": the guardian was written with an
   * answer nobody gave.
   */
  it("refuses a guardian whose legal authority nobody answered, without calling the API", async () => {
    const result = await addGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { hasLegalAuthority: undefined }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.hasLegalAuthority).toBeTruthy();
    expect(PATIENT_API.addGuardian).not.toHaveBeenCalled();
  });

  /**
   * A stepparent who brings the child every week and may not have the file.
   *
   * Control: guardianInputFrom — reading `hasLegalAuthority` from its own form field.
   * Replaced with the isPrimaryContact expression → red, "AssertionError: expected
   * "vi.fn()" to be called with arguments: [ …(2) ]" — the stepparent was sent as
   * authorised because she is the one who brings him.
   */
  it("does not grant legal authority to a guardian just for being the primary contact", async () => {
    await addGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { isPrimaryContact: "yes", hasLegalAuthority: "no" }),
    );

    expect(PATIENT_API.addGuardian).toHaveBeenCalledWith(
      PATIENT,
      expect.objectContaining({ isPrimaryContact: true, hasLegalAuthority: false }),
    );
  });

  /**
   * And the reverse: a non-custodial parent who holds the authority and is not the
   * contact. An unchecked checkbox sends nothing, which must read as false rather than
   * as an error.
   */
  it("records legal authority for a guardian who is not the primary contact", async () => {
    await addGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { isPrimaryContact: undefined, hasLegalAuthority: "yes" }),
    );

    expect(PATIENT_API.addGuardian).toHaveBeenCalledWith(
      PATIENT,
      expect.objectContaining({ isPrimaryContact: false, hasLegalAuthority: true }),
    );
  });

  it("echoes what was typed back when validation fails", async () => {
    const result = await addGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { firstName: "   " }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.firstName).toBeTruthy();
    expect(result.values?.lastName).toBe("Reyes");
    expect(result.values?.hasLegalAuthority).toBe("yes");
  });

  /**
   * React 19 resets an uncontrolled form after an action, and the form remounts when the
   * echoed `values` identity changes. Echoing a saved guardian back would refill every
   * field, and the next guardian would start as a near-duplicate of the last — on a form
   * whose whole point is that two adults are different people (D062's reasoning, applied
   * where the consequence is a legal one).
   */
  it("does not echo anything back after a successful add", async () => {
    const result = await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(result.status).toBe("saved");
    expect(result.values).toBeUndefined();
  });

  it("refreshes the patient page so the new guardian appears", async () => {
    await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(api.revalidatePath).toHaveBeenCalledWith(`/patients/${PATIENT}`);
  });

  it("stays on the patient page rather than navigating", async () => {
    await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(api.redirect).not.toHaveBeenCalled();
  });

  /**
   * A patient belonging to another provider and a patient that does not exist both arrive
   * as null, because the API answers 404 to each (D052). The action must not invent a
   * distinction the API deliberately refused to make.
   */
  it("says nothing about whether an unreachable patient exists", async () => {
    PATIENT_API.addGuardian.mockResolvedValue(null);

    const result = await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
    expect(api.revalidatePath).not.toHaveBeenCalled();
  });

  it("does not call the API without a patient", async () => {
    const result = await addGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { patientPublicId: "" }),
    );

    expect(result.status).toBe("error");
    expect(PATIENT_API.addGuardian).not.toHaveBeenCalled();
  });

  it("does not surface a raw transport error", async () => {
    PATIENT_API.addGuardian.mockRejectedValue(
      new Error("Patient API /patients/x/guardians failed with 503"),
    );

    const result = await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(result.status).toBe("error");
    expect(result.message).toBeTruthy();
    expect(result.message).not.toMatch(/503|Patient API/);
  });

  /** A guardian is a named adult attached to a named child. None of it may be logged. */
  it("logs nothing", async () => {
    const spies = [
      vi.spyOn(console, "log").mockImplementation(() => {}),
      vi.spyOn(console, "info").mockImplementation(() => {}),
      vi.spyOn(console, "warn").mockImplementation(() => {}),
      vi.spyOn(console, "error").mockImplementation(() => {}),
    ];

    PATIENT_API.addGuardian.mockRejectedValue(new Error("boom"));
    await addGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    for (const spy of spies) expect(spy).not.toHaveBeenCalled();
    for (const spy of spies) spy.mockRestore();
  });
});

describe("updateGuardian", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    PATIENT_API.updateGuardian.mockResolvedValue({ publicId: PATIENT });
  });

  it("sends the edit against both identifiers", async () => {
    const result = await updateGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { guardianPublicId: GUARDIAN, relationship: "Stepmother" }),
    );

    expect(result.status).toBe("saved");
    expect(PATIENT_API.updateGuardian).toHaveBeenCalledWith(
      PATIENT,
      GUARDIAN,
      expect.objectContaining({ relationship: "Stepmother", hasLegalAuthority: true }),
    );
  });

  /**
   * Withdrawing authority is as ordinary an edit as granting it — custody changes — and
   * has to travel as its own answer rather than as an omission.
   *
   * Control: guardianInputFrom — reading `hasLegalAuthority` from its own form field.
   * Replaced with the isPrimaryContact expression → red, "AssertionError: expected
   * "vi.fn()" to be called with arguments: [ …(3) ]": the withdrawal never left the BFF.
   */
  it("withdraws legal authority from a guardian who stays the primary contact", async () => {
    await updateGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, {
        guardianPublicId: GUARDIAN,
        isPrimaryContact: "yes",
        hasLegalAuthority: "no",
      }),
    );

    expect(PATIENT_API.updateGuardian).toHaveBeenCalledWith(
      PATIENT,
      GUARDIAN,
      expect.objectContaining({ isPrimaryContact: true, hasLegalAuthority: false }),
    );
  });

  /**
   * Control: updateGuardian — the `if (!patientPublicId || !guardianPublicId)` guard.
   * Deleted → red on the first assertion, "AssertionError: expected 'saved' to be
   * 'error'" — an edit was posted against an empty guardian id.
   */
  it("does not call the API without both identifiers", async () => {
    const result = await updateGuardian(INITIAL_GUARDIAN_STATE, formOf(GUARDIAN_FIELDS));

    expect(result.status).toBe("error");
    expect(PATIENT_API.updateGuardian).not.toHaveBeenCalled();
  });

  it("refuses an edit that leaves legal authority unanswered", async () => {
    const result = await updateGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, {
        guardianPublicId: GUARDIAN,
        hasLegalAuthority: undefined,
      }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.hasLegalAuthority).toBeTruthy();
    expect(PATIENT_API.updateGuardian).not.toHaveBeenCalled();
  });

  /** 404 covers both "not yours" and "not there". The message must cover neither. */
  it("says nothing about whether an unreachable guardian exists", async () => {
    PATIENT_API.updateGuardian.mockResolvedValue(null);

    const result = await updateGuardian(
      INITIAL_GUARDIAN_STATE,
      formOf(GUARDIAN_FIELDS, { guardianPublicId: GUARDIAN }),
    );

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });
});

describe("recordAddress", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    PATIENT_API.addAddress.mockResolvedValue({ publicId: PATIENT });
  });

  it("records a move with its type and the date it took effect", async () => {
    const result = await recordAddress(INITIAL_ADDRESS_STATE, formOf(ADDRESS_FIELDS));

    expect(result.status).toBe("saved");
    expect(PATIENT_API.addAddress).toHaveBeenCalledWith(PATIENT, {
      line1: "14 Elm Street",
      line2: null,
      city: "Towson",
      state: "MD",
      postalCode: "21204",
      addressType: "Session",
      notes: "Gate code 4821",
      effectiveFrom: "2026-08-25",
    });
  });

  /**
   * Control: recordAddress — the `Object.keys(errors).length > 0` gate before
   * patientsApi.addAddress. Deleted → red on the first assertion, "AssertionError:
   * expected 'saved' to be 'error'".
   */
  it("does not send an address the aggregate would throw on", async () => {
    const result = await recordAddress(
      INITIAL_ADDRESS_STATE,
      formOf(ADDRESS_FIELDS, { state: "Maryland" }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.state).toBeTruthy();
    expect(PATIENT_API.addAddress).not.toHaveBeenCalled();
  });

  it("refreshes the patient page so the move appears", async () => {
    await recordAddress(INITIAL_ADDRESS_STATE, formOf(ADDRESS_FIELDS));

    expect(api.revalidatePath).toHaveBeenCalledWith(`/patients/${PATIENT}`);
  });

  it("says nothing about whether an unreachable patient exists", async () => {
    PATIENT_API.addAddress.mockResolvedValue(null);

    const result = await recordAddress(INITIAL_ADDRESS_STATE, formOf(ADDRESS_FIELDS));

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });
});

describe("correctAddress", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    PATIENT_API.correctAddress.mockResolvedValue({ publicId: PATIENT });
  });

  /**
   * A CORRECTION IS NOT A MOVE.
   *
   * The body carries no address type and no effective date, so it cannot supersede
   * anything and cannot turn a session address into a billing one. That absence is the
   * guard: the fields are not trusted to be left alone, they are fields the request has no
   * room for. The form does not render them and the action does not read them, and this
   * test sends them anyway — because "the UI does not send it" is not a rule.
   *
   * Control: correctAddress — the use of validateAddressCorrection rather than
   * validateNewAddress. Swapped, with the two fields read from the form → red,
   * "AssertionError: expected "vi.fn()" to be called with arguments: [ …(3) ]": the body
   * arrived carrying addressType "Billing" and an effective date of 2020.
   */
  it("fixes a typo without carrying a type or a date, even when sent them", async () => {
    const result = await correctAddress(
      INITIAL_ADDRESS_CORRECTION_STATE,
      formOf(ADDRESS_FIELDS, {
        addressPublicId: ADDRESS,
        line1: "14 Elm Street",
        addressType: "Billing",
        effectiveFrom: "2020-01-01",
      }),
    );

    expect(result.status).toBe("saved");
    expect(PATIENT_API.correctAddress).toHaveBeenCalledWith(PATIENT, ADDRESS, {
      line1: "14 Elm Street",
      line2: null,
      city: "Towson",
      state: "MD",
      postalCode: "21204",
      notes: "Gate code 4821",
    });

    const body = PATIENT_API.correctAddress.mock.calls[0]?.[2];
    expect(body).not.toHaveProperty("addressType");
    expect(body).not.toHaveProperty("effectiveFrom");
  });

  /**
   * Control: correctAddress — the `if (!patientPublicId || !addressPublicId)` guard.
   * Deleted → red on the first assertion, "AssertionError: expected 'saved' to be
   * 'error'".
   */
  it("does not call the API without both identifiers", async () => {
    const result = await correctAddress(
      INITIAL_ADDRESS_CORRECTION_STATE,
      formOf(ADDRESS_FIELDS),
    );

    expect(result.status).toBe("error");
    expect(PATIENT_API.correctAddress).not.toHaveBeenCalled();
  });

  it("echoes the correction back when validation fails", async () => {
    const result = await correctAddress(
      INITIAL_ADDRESS_CORRECTION_STATE,
      formOf(ADDRESS_FIELDS, { addressPublicId: ADDRESS, postalCode: "abcde" }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.postalCode).toBeTruthy();
    expect(result.values?.line1).toBe("14 Elm Street");
    expect(PATIENT_API.correctAddress).not.toHaveBeenCalled();
  });

  it("says nothing about whether an unreachable address exists", async () => {
    PATIENT_API.correctAddress.mockResolvedValue(null);

    const result = await correctAddress(
      INITIAL_ADDRESS_CORRECTION_STATE,
      formOf(ADDRESS_FIELDS, { addressPublicId: ADDRESS }),
    );

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });
});
