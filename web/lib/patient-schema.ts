/**
 * New-patient validation for the BFF.
 *
 * Mirrors the domain invariants in Practice.Domain.Patients.Patient. The duplication is
 * deliberate and one-directional: this produces messages a clinician can act on, the
 * domain enforces the rule. If the two ever disagree, the domain wins — it is the one
 * no caller can bypass.
 */

export interface NewPatientInput {
  firstName: string;
  lastName: string;
  /** ISO yyyy-mm-dd, as an <input type="date"> produces. */
  dateOfBirth: string;
  clinicalSummary: string;
}

export type NewPatientErrors = Partial<Record<keyof NewPatientInput, string>>;

/** The practice serves birth to 5. Beyond 25 years is a typo, not a patient. */
const IMPLAUSIBLE_YEARS = 25;

export function validateNewPatient(
  input: NewPatientInput,
  today: Date,
): {
  errors: NewPatientErrors;
  value: {
    firstName: string;
    lastName: string;
    dateOfBirth: string;
    clinicalSummary: string | null;
  };
} {
  const errors: NewPatientErrors = {};

  const firstName = input.firstName?.trim() ?? "";
  const lastName = input.lastName?.trim() ?? "";
  const summary = input.clinicalSummary?.trim() ?? "";
  const dob = input.dateOfBirth?.trim() ?? "";

  if (!firstName) errors.firstName = "Enter the child's first name.";
  else if (firstName.length > 100) errors.firstName = "Please keep this under 100 characters.";

  if (!lastName) errors.lastName = "Enter the child's last name.";
  else if (lastName.length > 100) errors.lastName = "Please keep this under 100 characters.";

  if (summary.length > 4000) {
    errors.clinicalSummary = "Please keep this under 4000 characters.";
  }

  const dateProblem = dateOfBirthError(dob, today);
  if (dateProblem) errors.dateOfBirth = dateProblem;

  return {
    errors,
    value: {
      firstName,
      lastName,
      dateOfBirth: dob,
      clinicalSummary: summary || null,
    },
  };
}

/**
 * Everything this application believes about a child's date of birth, in one place.
 *
 * Extracted when the consultation inbox gained a second way to create a patient. Two
 * copies would be two things to keep in step, and the copy that drifted would be the one
 * accepting a birthdate the other form calls a typo — into the field early-intervention
 * eligibility is computed from.
 *
 * @param dateOfBirth already trimmed. "" means the field was left blank.
 * @returns the message, or undefined when the date is fine.
 */
export function dateOfBirthError(
  dateOfBirth: string,
  today: Date,
): string | undefined {
  if (!dateOfBirth) return "Enter the child's date of birth.";

  // Parsed as UTC so a browser west of Greenwich cannot shift the date by a day.
  const parsed = new Date(`${dateOfBirth}T00:00:00Z`);

  if (Number.isNaN(parsed.getTime())) return "That date does not look right.";
  if (parsed.getTime() > today.getTime()) {
    return "A date of birth cannot be in the future.";
  }

  const earliest = new Date(today);
  earliest.setUTCFullYear(earliest.getUTCFullYear() - IMPLAUSIBLE_YEARS);

  if (parsed.getTime() < earliest.getTime()) {
    /*
     * Phrased as a question, not a rejection.
     *
     * The likely cause is a mistyped year, and telling a clinician their entry is
     * "invalid" is less useful than telling them what looked wrong.
     */
    return "That looks like a typo — this practice serves children from birth to 5.";
  }

  return undefined;
}

/**
 * Turning a consultation enquiry into a patient.
 *
 * TWO FIELDS, because the enquiry already holds the rest. The public form asks a first
 * name and an age in months and nothing else about the child, so a surname and a date of
 * birth have to be typed — and the date is NOT derived from the age. A birthdate computed
 * from a parent's rounded estimate is a value nobody stated, sitting in the field that
 * every clinical decision here is measured against.
 *
 * There is deliberately no clinical summary: a parent's description of their child's
 * difficulties is not a clinician's assessment, and copying one into the other would put
 * words in the record that nobody with a licence wrote.
 */
export interface ConversionInput {
  lastName: string;
  /** ISO yyyy-mm-dd, as an <input type="date"> produces. */
  dateOfBirth: string;
}

export type ConversionErrors = Partial<Record<keyof ConversionInput, string>>;

export function validateConversion(
  input: ConversionInput,
  today: Date,
): { errors: ConversionErrors; value: ConversionInput } {
  const errors: ConversionErrors = {};

  const lastName = input.lastName?.trim() ?? "";
  const dateOfBirth = input.dateOfBirth?.trim() ?? "";

  if (!lastName) errors.lastName = "Enter the child's last name.";
  else if (lastName.length > 100) errors.lastName = "Please keep this under 100 characters.";

  const dateProblem = dateOfBirthError(dateOfBirth, today);
  if (dateProblem) errors.dateOfBirth = dateProblem;

  return { errors, value: { lastName, dateOfBirth } };
}
