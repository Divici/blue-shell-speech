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

  if (!dob) {
    errors.dateOfBirth = "Enter the child's date of birth.";
  } else {
    // Parsed as UTC so a browser west of Greenwich cannot shift the date by a day.
    const parsed = new Date(`${dob}T00:00:00Z`);

    if (Number.isNaN(parsed.getTime())) {
      errors.dateOfBirth = "That date does not look right.";
    } else if (parsed.getTime() > today.getTime()) {
      errors.dateOfBirth = "A date of birth cannot be in the future.";
    } else {
      const earliest = new Date(today);
      earliest.setUTCFullYear(earliest.getUTCFullYear() - IMPLAUSIBLE_YEARS);

      if (parsed.getTime() < earliest.getTime()) {
        /*
         * Phrased as a question, not a rejection.
         *
         * The likely cause is a mistyped year, and telling a clinician their entry is
         * "invalid" is less useful than telling them what looked wrong.
         */
        errors.dateOfBirth =
          "That looks like a typo — this practice serves children from birth to 5.";
      }
    }
  }

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
