import { describe, it, expect } from "vitest";
import {
  validateGoal,
  GOAL_DOMAINS,
  CUE_LEVELS,
  AAC_MODALITIES,
  isAacDomain,
  type GoalInput,
} from "./goal-schema";

/**
 * Goal validation for the BFF.
 *
 * The rule with teeth is the AAC one: the aggregate throws on AAC details attached to a
 * non-AAC goal, and CK_Goals_AacFieldsOnlyOnAacGoals refuses the row independently. This
 * layer exists to say the same thing in a sentence a clinician can act on — NOT to
 * invent a third rule. Where the three could disagree, the domain wins.
 */

function input(overrides: Partial<GoalInput> = {}): GoalInput {
  return {
    goalText: "Produce /s/ in the initial position of words.",
    domain: "Articulation",
    targetCriteria: "80% accuracy over 3 consecutive sessions",
    cueLevelExpected: "Verbal",
    startDate: "2026-08-24",
    aacModality: "",
    aacDeviceNotes: "",
    ...overrides,
  };
}

describe("validateGoal", () => {
  it("accepts a complete non-AAC goal and normalises it for the API", () => {
    const { errors, value } = validateGoal(input());

    expect(errors).toEqual({});
    expect(value).toEqual({
      goalText: "Produce /s/ in the initial position of words.",
      domain: "Articulation",
      targetCriteria: "80% accuracy over 3 consecutive sessions",
      cueLevelExpected: "Verbal",
      startDate: "2026-08-24",
      aacModality: null,
      aacDeviceNotes: null,
    });
  });

  it("requires the goal itself", () => {
    const { errors } = validateGoal(input({ goalText: "   " }));
    expect(errors.goalText).toBeTruthy();
  });

  it("trims the goal rather than storing the whitespace", () => {
    const { value } = validateGoal(input({ goalText: "  Request a break.  " }));
    expect(value.goalText).toBe("Request a break.");
  });

  /** nvarchar(1000) in the schema, Guard.MaxLength in the aggregate. */
  it("rejects a goal longer than the column", () => {
    const { errors } = validateGoal(input({ goalText: "a".repeat(1001) }));
    expect(errors.goalText).toBeTruthy();
  });

  it("rejects target criteria longer than the column", () => {
    const { errors } = validateGoal(input({ targetCriteria: "b".repeat(501) }));
    expect(errors.targetCriteria).toBeTruthy();
  });

  /**
   * TargetCriteria is free text on purpose (presearch §5.4). Anything that tried to parse
   * "80% accuracy over 3 consecutive sessions" into a rule would be wrong for the next goal.
   */
  it("does not try to parse target criteria", () => {
    const { errors, value } = validateGoal(
      input({ targetCriteria: "4 of 5 opportunities, generalised to the classroom" }),
    );

    expect(errors).toEqual({});
    expect(value.targetCriteria).toBe("4 of 5 opportunities, generalised to the classroom");
  });

  it("treats blank optional fields as absent rather than empty strings", () => {
    const { value } = validateGoal(
      input({ targetCriteria: "  ", cueLevelExpected: "" }),
    );

    expect(value.targetCriteria).toBeNull();
    expect(value.cueLevelExpected).toBeNull();
  });

  it("requires a start date", () => {
    const { errors } = validateGoal(input({ startDate: "" }));
    expect(errors.startDate).toBeTruthy();
  });

  it("rejects a start date it cannot read", () => {
    const { errors } = validateGoal(input({ startDate: "24/08/2026" }));
    expect(errors.startDate).toBeTruthy();
  });

  /*
   * A hand-posted value that is not in the enum would reach the API as a 400 with a
   * serialisation message. Rejecting it here keeps the failure readable, and keeps the
   * option list and the validator from drifting apart.
   */
  it("rejects a domain that is not in the enum", () => {
    const { errors } = validateGoal(input({ domain: "Telepathy" }));
    expect(errors.domain).toBeTruthy();
  });

  it("rejects a cue level that is not in the enum", () => {
    const { errors } = validateGoal(input({ cueLevelExpected: "Shouting" }));
    expect(errors.cueLevelExpected).toBeTruthy();
  });

  it("accepts every domain the enum defines", () => {
    for (const domain of GOAL_DOMAINS) {
      const { errors } = validateGoal(input({ domain: domain.value }));
      expect(errors, domain.value).toEqual({});
    }
  });

  it("accepts every cue level the enum defines", () => {
    for (const cue of CUE_LEVELS) {
      const { errors } = validateGoal(input({ cueLevelExpected: cue.value }));
      expect(errors, cue.value).toEqual({});
    }
  });

  // --------------------------------------------------------------- AAC rule

  describe("AAC details", () => {
    it("accepts them on an AAC goal", () => {
      const { errors, value } = validateGoal(
        input({
          domain: "Aac",
          aacModality: "HighTech",
          aacDeviceNotes: "Speech-generating app on the family tablet.",
        }),
      );

      expect(errors).toEqual({});
      expect(value.aacModality).toBe("HighTech");
      expect(value.aacDeviceNotes).toBe("Speech-generating app on the family tablet.");
    });

    /**
     * The aggregate throws here and the CHECK refuses the row. This must refuse too —
     * and must REFUSE rather than quietly blank the fields, because silently discarding
     * something a clinician typed is the failure mode that never gets noticed.
     */
    it("are refused on a goal that is not AAC", () => {
      const { errors } = validateGoal(
        input({ domain: "Articulation", aacModality: "HighTech" }),
      );

      expect(errors.aacModality).toBeTruthy();
    });

    it("are refused when only the device notes are supplied", () => {
      const { errors } = validateGoal(
        input({ domain: "Feeding", aacDeviceNotes: "Picture board." }),
      );

      expect(errors.aacDeviceNotes ?? errors.aacModality).toBeTruthy();
    });

    /** Whitespace is not an AAC detail. The form posts "" for a field never filled in. */
    it("are not triggered by blank fields on a non-AAC goal", () => {
      const { errors } = validateGoal(
        input({ domain: "Fluency", aacModality: "", aacDeviceNotes: "   " }),
      );

      expect(errors).toEqual({});
    });

    /** Nullable in the model: an AAC goal may exist before the device is chosen. */
    it("are optional on an AAC goal", () => {
      const { errors, value } = validateGoal(
        input({ domain: "Aac", aacModality: "", aacDeviceNotes: "" }),
      );

      expect(errors).toEqual({});
      expect(value.aacModality).toBeNull();
      expect(value.aacDeviceNotes).toBeNull();
    });

    it("rejects a modality that is not in the enum", () => {
      const { errors } = validateGoal(input({ domain: "Aac", aacModality: "Telepathy" }));
      expect(errors.aacModality).toBeTruthy();
    });

    it("rejects device notes longer than the column", () => {
      const { errors } = validateGoal(
        input({ domain: "Aac", aacDeviceNotes: "c".repeat(501) }),
      );

      expect(errors.aacDeviceNotes).toBeTruthy();
    });

    it("accepts every modality the enum defines", () => {
      for (const modality of AAC_MODALITIES) {
        const { errors } = validateGoal(
          input({ domain: "Aac", aacModality: modality.value }),
        );
        expect(errors, modality.value).toEqual({});
      }
    });
  });

  /**
   * One predicate, used by the form to decide what to show and by the validator to decide
   * what to allow. Two independent notions of "is this AAC" is how a UI ends up
   * disagreeing with a CHECK constraint.
   */
  describe("isAacDomain", () => {
    it("is true only for the AAC domain", () => {
      expect(isAacDomain("Aac")).toBe(true);

      for (const domain of GOAL_DOMAINS.filter((d) => d.value !== "Aac")) {
        expect(isAacDomain(domain.value), domain.value).toBe(false);
      }
    });

    /** The enum value is "Aac"; the label is "AAC". Neither spelling may be guessed at. */
    it("does not match on the label's spelling", () => {
      expect(isAacDomain("AAC")).toBe(false);
      expect(isAacDomain("")).toBe(false);
    });
  });
});
