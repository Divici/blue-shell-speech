/**
 * Treatment-goal options and validation for the BFF.
 *
 * Client-safe on purpose. The enum values, the labels and the AAC predicate are needed by
 * the form in the browser AND by the server action, and a single source for all three is
 * what stops the UI offering a choice the API would reject.
 *
 * Mirrors Practice.Domain.Goals.Goal. The duplication is deliberate and one-directional,
 * exactly as in patient-schema.ts: this layer produces messages a clinician can act on,
 * the aggregate enforces the invariant, and the database enforces it again. If the three
 * ever disagree, the domain wins — it is the one no caller can bypass.
 */

/** The persisted enum value paired with the words a clinician reads. */
export interface GoalOption {
  value: string;
  label: string;
}

/**
 * GoalDomain. Order matches the enum, which is ordered as the domain file declares it —
 * not alphabetically, because the enum's integers are the persisted values.
 *
 * "Aac" is the enum member; "AAC" is the term. The two spellings are never interchanged.
 */
export const GOAL_DOMAINS: readonly GoalOption[] = [
  { value: "Articulation", label: "Articulation" },
  { value: "ReceptiveLanguage", label: "Receptive language" },
  { value: "ExpressiveLanguage", label: "Expressive language" },
  { value: "SocialCommunication", label: "Social communication" },
  { value: "Fluency", label: "Fluency" },
  { value: "Feeding", label: "Feeding" },
  { value: "Aac", label: "AAC" },
] as const;

/**
 * CueLevel, most independent first.
 *
 * The order is the whole point: "improving" means moving up this list, and a picker that
 * sorted these alphabetically would hide the one property that makes the value clinical.
 */
export const CUE_LEVELS: readonly GoalOption[] = [
  { value: "Independent", label: "Independent" },
  { value: "Visual", label: "Visual cues" },
  { value: "Gestural", label: "Gestural cues" },
  { value: "Verbal", label: "Verbal cues" },
  { value: "Tactile", label: "Tactile cues" },
  { value: "HandOverHand", label: "Hand over hand" },
];

/** AacModality. Only ever offered on an AAC goal. */
export const AAC_MODALITIES: readonly GoalOption[] = [
  { value: "HighTech", label: "High-tech (speech-generating device or app)" },
  { value: "LowTech", label: "Low-tech (picture board, communication book)" },
  { value: "PECS", label: "PECS" },
  { value: "Sign", label: "Sign" },
  { value: "Hybrid", label: "Hybrid" },
];

export const GOAL_STATUS_LABELS: Record<string, string> = {
  Active: "Active",
  Met: "Met",
  Discontinued: "Discontinued",
  OnHold: "On hold",
};

export const GOAL_DOMAIN_LABELS: Record<string, string> = Object.fromEntries(
  GOAL_DOMAINS.map((d) => [d.value, d.label]),
);

export const CUE_LEVEL_LABELS: Record<string, string> = Object.fromEntries(
  CUE_LEVELS.map((c) => [c.value, c.label]),
);

export const AAC_MODALITY_LABELS: Record<string, string> = Object.fromEntries(
  AAC_MODALITIES.map((m) => [m.value, m.label]),
);

/**
 * The one place that decides whether a goal is an AAC goal.
 *
 * The form asks this to decide what to render; the validator asks it to decide what to
 * allow; the aggregate and CK_Goals_AacFieldsOnlyOnAacGoals ask the same question of the
 * stored integer. Two independent notions of "is this AAC" is precisely how a UI ends up
 * offering a combination the database refuses.
 */
export function isAacDomain(domain: string): boolean {
  return domain === "Aac";
}

export interface GoalInput {
  goalText: string;
  domain: string;
  targetCriteria: string;
  cueLevelExpected: string;
  /** ISO yyyy-mm-dd, as an <input type="date"> produces. */
  startDate: string;
  aacModality: string;
  aacDeviceNotes: string;
}

export type GoalErrors = Partial<Record<keyof GoalInput, string>>;

/** Exactly the body the API's CreateGoalRequest expects. */
export interface GoalValue {
  goalText: string;
  domain: string;
  startDate: string;
  targetCriteria: string | null;
  cueLevelExpected: string | null;
  aacModality: string | null;
  aacDeviceNotes: string | null;
}

/** Column widths, so a rejection happens here rather than as a truncation downstream. */
const MAX_GOAL_TEXT = 1000;
const MAX_TARGET_CRITERIA = 500;
const MAX_AAC_DEVICE_NOTES = 500;

/**
 * The wording the aggregate uses, in a clinician's register.
 *
 * Shown only to a caller that bypassed the form — the form unmounts these fields on a
 * non-AAC domain, so there is nothing to submit. It exists because "the UI hides it" is
 * not a rule (CLAUDE.md non-negotiable #6, applied to data as well as authorization).
 */
const AAC_ON_NON_AAC =
  "AAC details belong on an AAC goal. Change the domain to AAC, or clear these.";

export function validateGoal(input: GoalInput): { errors: GoalErrors; value: GoalValue } {
  const errors: GoalErrors = {};

  const goalText = input.goalText?.trim() ?? "";
  const domain = input.domain?.trim() ?? "";
  const targetCriteria = input.targetCriteria?.trim() ?? "";
  const cueLevelExpected = input.cueLevelExpected?.trim() ?? "";
  const startDate = input.startDate?.trim() ?? "";
  const aacModality = input.aacModality?.trim() ?? "";
  const aacDeviceNotes = input.aacDeviceNotes?.trim() ?? "";

  if (!goalText) {
    errors.goalText = "Write the goal.";
  } else if (goalText.length > MAX_GOAL_TEXT) {
    errors.goalText = `Please keep this under ${MAX_GOAL_TEXT} characters.`;
  }

  if (!GOAL_DOMAINS.some((d) => d.value === domain)) {
    errors.domain = "Choose a domain.";
  }

  if (targetCriteria.length > MAX_TARGET_CRITERIA) {
    errors.targetCriteria = `Please keep this under ${MAX_TARGET_CRITERIA} characters.`;
  }

  if (cueLevelExpected && !CUE_LEVELS.some((c) => c.value === cueLevelExpected)) {
    errors.cueLevelExpected = "Choose a cue level from the list.";
  }

  if (!startDate) {
    errors.startDate = "Pick the date this goal starts.";
  } else if (Number.isNaN(new Date(`${startDate}T00:00:00Z`).getTime())) {
    // Parsed as UTC so a browser west of Greenwich cannot shift the date by a day.
    errors.startDate = "That date does not look right.";
  }

  /*
   * AAC details only exist on an AAC goal.
   *
   * Refused, not silently blanked. Quietly discarding something a clinician typed is the
   * failure mode nobody notices — and the aggregate throws rather than dropping them, so
   * dropping them here would be a third, softer rule that the other two do not have.
   */
  if (!isAacDomain(domain)) {
    if (aacModality) errors.aacModality = AAC_ON_NON_AAC;
    if (aacDeviceNotes) errors.aacDeviceNotes = AAC_ON_NON_AAC;
  } else {
    if (aacModality && !AAC_MODALITIES.some((m) => m.value === aacModality)) {
      errors.aacModality = "Choose a modality from the list.";
    }
    if (aacDeviceNotes.length > MAX_AAC_DEVICE_NOTES) {
      errors.aacDeviceNotes = `Please keep this under ${MAX_AAC_DEVICE_NOTES} characters.`;
    }
  }

  return {
    errors,
    value: {
      goalText,
      domain,
      startDate,
      targetCriteria: targetCriteria || null,
      cueLevelExpected: cueLevelExpected || null,
      // Null on a non-AAC goal regardless: the errors above already stopped the write,
      // and a value shape that could carry them past the guard is a shape worth not having.
      aacModality: isAacDomain(domain) ? aacModality || null : null,
      aacDeviceNotes: isAacDomain(domain) ? aacDeviceNotes || null : null,
    },
  };
}
