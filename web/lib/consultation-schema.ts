/**
 * Consultation request validation.
 *
 * Runs on the SERVER. Client-side validation on this form is a convenience for the
 * person filling it in; it is not a control, and nothing here may be skipped because
 * the browser already checked (docs/SECURITY.md).
 *
 * This form collects a child's first name and developmental concerns. It is not yet PHI
 * — it is a prospect enquiry from a parent who is not a patient — but it is treated as
 * PHI-adjacent and stored under the same controls (docs/DATA_MODEL.md).
 */

export interface ConsultationInput {
  parentName: string;
  email: string;
  phone: string;
  childFirstName: string;
  childAgeMonths: string;
  concerns: string;
  preferredContact: string;
  /** Honeypot. Real people leave it empty; naive bots fill every field. */
  website?: string;
}

export type ConsultationErrors = Partial<Record<keyof ConsultationInput, string>>;

export const PREFERRED_CONTACT_OPTIONS = ["Email", "Phone", "Either"] as const;

/** Birth to 5 years is the practice's population — 0 to 71 months inclusive. */
export const MAX_CHILD_AGE_MONTHS = 71;

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

const LIMITS = {
  parentName: 120,
  email: 254,
  phone: 32,
  childFirstName: 60,
  concerns: 2000,
} as const;

/**
 * Validates and normalises. Returns trimmed values so a stray trailing space cannot
 * produce two "different" enquiries from the same parent.
 */
/**
 * The normalised result. `childAgeMonths` is a number here, unlike the string that
 * arrives from the form — so it must be omitted before being re-declared, or the
 * intersection collapses `string & number` to `never`.
 */
export type ValidatedConsultation = Omit<
  ConsultationInput,
  "website" | "childAgeMonths"
> & { childAgeMonths: number };

export function validateConsultation(input: ConsultationInput): {
  errors: ConsultationErrors;
  value: ValidatedConsultation;
} {
  const errors: ConsultationErrors = {};

  const parentName = input.parentName?.trim() ?? "";
  const email = input.email?.trim() ?? "";
  const phone = input.phone?.trim() ?? "";
  const childFirstName = input.childFirstName?.trim() ?? "";
  const concerns = input.concerns?.trim() ?? "";
  const preferredContact = input.preferredContact?.trim() ?? "";
  const ageRaw = input.childAgeMonths?.trim() ?? "";

  if (!parentName) {
    errors.parentName = "Please enter your name.";
  } else if (parentName.length > LIMITS.parentName) {
    errors.parentName = `Please keep this under ${LIMITS.parentName} characters.`;
  }

  if (!email) {
    errors.email = "Please enter an email address so we can reply.";
  } else if (email.length > LIMITS.email || !EMAIL.test(email)) {
    errors.email = "That doesn't look like an email address.";
  }

  // Phone is optional unless it is the chosen way to be reached — asking for a phone
  // number someone does not want to be called on is how a form loses a parent.
  if (phone && phone.length > LIMITS.phone) {
    errors.phone = `Please keep this under ${LIMITS.phone} characters.`;
  }
  if (!phone && (preferredContact === "Phone" || preferredContact === "Either")) {
    errors.phone = "Please add a phone number, or choose email instead.";
  }

  if (!childFirstName) {
    errors.childFirstName = "Please enter your child's first name.";
  } else if (childFirstName.length > LIMITS.childFirstName) {
    errors.childFirstName = `Please keep this under ${LIMITS.childFirstName} characters.`;
  }

  const childAgeMonths = Number.parseInt(ageRaw, 10);
  if (!ageRaw) {
    errors.childAgeMonths = "Please enter your child's age in months.";
  } else if (!Number.isInteger(childAgeMonths) || childAgeMonths < 0) {
    errors.childAgeMonths = "Please enter an age in whole months.";
  } else if (childAgeMonths > MAX_CHILD_AGE_MONTHS) {
    // Not an error in the parent's behaviour — the practice serves birth to 5.
    errors.childAgeMonths =
      "This practice serves children from birth to 5 years. Please get in touch anyway and we'll point you in the right direction.";
  }

  if (!concerns) {
    errors.concerns = "Please tell us a little about what you're noticing.";
  } else if (concerns.length > LIMITS.concerns) {
    errors.concerns = `Please keep this under ${LIMITS.concerns} characters.`;
  }

  if (!PREFERRED_CONTACT_OPTIONS.includes(preferredContact as never)) {
    errors.preferredContact = "Please choose how you'd like to be contacted.";
  }

  return {
    errors,
    value: {
      parentName,
      email,
      phone,
      childFirstName,
      childAgeMonths: Number.isNaN(childAgeMonths) ? -1 : childAgeMonths,
      concerns,
      preferredContact,
    },
  };
}

/** True when a submission looks automated and should be silently accepted, not stored. */
export function isLikelyBot(input: ConsultationInput): boolean {
  return Boolean(input.website?.trim());
}
