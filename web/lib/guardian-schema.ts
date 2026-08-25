/**
 * Guardian validation for the BFF.
 *
 * Client-safe on purpose. The form in the browser and the server action need the same
 * rules, and a single source for both is what stops the UI offering something the API
 * would refuse.
 *
 * Mirrors Practice.Domain.Patients.Guardian. The duplication is deliberate and
 * one-directional, exactly as in patient-schema.ts and goal-schema.ts: this layer produces
 * messages a clinician can act on, the aggregate enforces the invariant, and the database
 * enforces what it can. If the three ever disagree, the domain wins — it is the one no
 * caller can bypass.
 */

/**
 * The legal-authority answer, as a form can carry it.
 *
 * THREE VALUES, NOT A BOOLEAN — and that is the whole point. `HasLegalAuthority` is a bit
 * with no room for "nobody said", so the distinction has to live here, on the way in. A
 * checkbox would submit `false` for both "she may not" and "nobody looked", and the two
 * are not the same claim about a family. The empty string is refused rather than resolved.
 */
export type LegalAuthorityAnswer = "" | "yes" | "no";

export interface GuardianInput {
  firstName: string;
  lastName: string;
  /** Free text, as the column is. Mother, Father, Stepfather, Grandparent, Foster carer… */
  relationship: string;
  phone: string;
  email: string;
  isPrimaryContact: boolean;
  /** Deliberately a string: see LegalAuthorityAnswer. */
  hasLegalAuthority: string;
}

export type GuardianErrors = Partial<Record<keyof GuardianInput, string>>;

/** Exactly the body the API's AddGuardianRequest and UpdateGuardianRequest expect. */
export interface GuardianValue {
  firstName: string;
  lastName: string;
  relationship: string;
  phone: string | null;
  email: string | null;
  isPrimaryContact: boolean;
  /**
   * Undefined when the question was not answered — never false.
   *
   * The action refuses to call the API in that case, and the API refuses a null of its own
   * (PatientEndpoints.AuthorityNotStated). Three layers, one question.
   */
  hasLegalAuthority?: boolean;
}

/** Column widths, so a rejection happens here rather than as a truncation downstream. */
const MAX_NAME = 100;
const MAX_RELATIONSHIP = 50;
const MAX_PHONE = 50;
const MAX_EMAIL = 256;

/**
 * Deliberately loose. Email validation by regular expression is a well-known way to reject
 * addresses that work; this only catches the shapes that cannot be addresses at all, and
 * the real check is whether a message arrives.
 */
const EMAIL_SHAPE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export const LEGAL_AUTHORITY_UNSTATED =
  "Say whether this person may receive the child's records. It is a separate question from "
  + "who the primary contact is.";

export const PRIMARY_NEEDS_CONTACT =
  "The primary contact needs a phone number or an email address.";

export function validateGuardian(input: GuardianInput): {
  errors: GuardianErrors;
  value: GuardianValue;
} {
  const errors: GuardianErrors = {};

  const firstName = input.firstName?.trim() ?? "";
  const lastName = input.lastName?.trim() ?? "";
  const relationship = input.relationship?.trim() ?? "";
  const phone = input.phone?.trim() ?? "";
  const email = input.email?.trim() ?? "";
  const authority = input.hasLegalAuthority?.trim() ?? "";

  if (!firstName) errors.firstName = "Enter their first name.";
  else if (firstName.length > MAX_NAME) {
    errors.firstName = `Please keep this under ${MAX_NAME} characters.`;
  }

  if (!lastName) errors.lastName = "Enter their last name.";
  else if (lastName.length > MAX_NAME) {
    errors.lastName = `Please keep this under ${MAX_NAME} characters.`;
  }

  if (!relationship) errors.relationship = "How are they related to the child?";
  else if (relationship.length > MAX_RELATIONSHIP) {
    errors.relationship = `Please keep this under ${MAX_RELATIONSHIP} characters.`;
  }

  if (phone.length > MAX_PHONE) {
    errors.phone = `Please keep this under ${MAX_PHONE} characters.`;
  }

  if (email) {
    if (email.length > MAX_EMAIL) {
      errors.email = `Please keep this under ${MAX_EMAIL} characters.`;
    } else if (!EMAIL_SHAPE.test(email)) {
      errors.email = "That does not look like an email address.";
    }
  }

  /*
   * Mirrors Guardian.Create and Guardian.UpdateContact, both of which throw.
   *
   * The primary contact is who Michelle calls when a session has to move. One with no
   * phone and no email is a record that looks complete and is not — and the failure only
   * shows up on the afternoon it matters.
   */
  if (input.isPrimaryContact && !phone && !email) {
    errors.phone = PRIMARY_NEEDS_CONTACT;
  }

  /*
   * NO DEFAULT, IN EITHER DIRECTION.
   *
   * Defaulting to yes would hand a child's file to whoever was typed in fastest.
   * Defaulting to no is the safer error and still an error: it records a decision nobody
   * made, and once written it is indistinguishable from one somebody did make. So the
   * answer is required, and the value carries `undefined` until there is one.
   */
  if (authority !== "yes" && authority !== "no") {
    errors.hasLegalAuthority = LEGAL_AUTHORITY_UNSTATED;
  }

  return {
    errors,
    value: {
      firstName,
      lastName,
      relationship,
      phone: phone || null,
      email: email || null,
      isPrimaryContact: input.isPrimaryContact,
      // Read from its own answer and from nothing else — never from isPrimaryContact.
      ...(authority === "yes" || authority === "no"
        ? { hasLegalAuthority: authority === "yes" }
        : {}),
    },
  };
}

/**
 * Who, if anyone, may receive this child's records.
 *
 * Named once so every part of the page renders on the same predicate rather than on a
 * locally reinvented condition (the rule D062 set for AAC fields, applied here).
 *
 * `none-authorised` is a REAL STATE, not an error. A family whose custody paperwork has
 * not arrived genuinely has nobody entitled to the file yet, and the honest thing is to
 * say so — loudly, because releasing to the wrong adult is a breach — rather than to pick
 * somebody. It is deliberately distinct from `no-guardians`, which is a record nobody has
 * filled in.
 */
export type RecordsReleaseState = "no-guardians" | "none-authorised" | "authorised";

export function recordsReleaseState(
  guardians: readonly { hasLegalAuthority: boolean }[],
): RecordsReleaseState {
  if (guardians.length === 0) return "no-guardians";
  return guardians.some((g) => g.hasLegalAuthority) ? "authorised" : "none-authorised";
}
