/**
 * Address validation for the BFF.
 *
 * Client-safe, and mirrors Practice.Domain.Patients.PatientAddress the same one-directional
 * way the other schema modules mirror their aggregates.
 *
 * TWO OPERATIONS, NOT ONE, because they are different events with different consequences:
 *
 * - **Recording a move** (`validateNewAddress`) closes the current address of that type and
 *   keeps it. A note describing a visit last spring refers to where the family lived then.
 * - **Fixing a typo** (`validateAddressCorrection`) changes one row in place. The family
 *   never lived at the mistyped address, so there is no history to preserve, and
 *   superseding would invent a move that never happened.
 *
 * Recording one as the other either fabricates a move or erases a real one, which is why
 * the two have separate shapes rather than a flag.
 */

export interface AddressOption {
  value: string;
  label: string;
}

/** AddressType. Exactly the two the domain enum has. */
export const ADDRESS_TYPES: readonly AddressOption[] = [
  { value: "Session", label: "Session — where therapy happens" },
  { value: "Billing", label: "Billing" },
] as const;

export const ADDRESS_TYPE_LABELS: Record<string, string> = {
  Session: "Session address",
  Billing: "Billing address",
};

interface AddressFields {
  line1: string;
  line2: string;
  city: string;
  state: string;
  postalCode: string;
  notes: string;
}

export interface NewAddressInput extends AddressFields {
  addressType: string;
  /** ISO yyyy-mm-dd, as an <input type="date"> produces. */
  effectiveFrom: string;
}

export type AddressCorrectionInput = AddressFields;

export type NewAddressErrors = Partial<Record<keyof NewAddressInput, string>>;
export type AddressCorrectionErrors = Partial<Record<keyof AddressCorrectionInput, string>>;

interface AddressValueFields {
  line1: string;
  line2: string | null;
  city: string;
  state: string;
  postalCode: string;
  notes: string | null;
}

/** Exactly the body the API's AddAddressRequest expects. */
export interface NewAddressValue extends AddressValueFields {
  addressType: string;
  effectiveFrom: string;
}

/**
 * Exactly the body the API's CorrectAddressRequest expects.
 *
 * NO addressType AND NO DATES, and the absence is the guard. They are not fields a caller
 * is trusted to leave alone; they are fields this shape cannot carry, so a hand-crafted
 * post has nothing to smuggle them in.
 */
export type AddressCorrectionValue = AddressValueFields;

const MAX_LINE = 200;
const MAX_CITY = 100;
const MAX_POSTAL = 20;
const MAX_NOTES = 500;

/** 21204 or 21204-1234. The practice is in Maryland and bills nobody abroad. */
const ZIP_SHAPE = /^\d{5}(-\d{4})?$/;

function validateFields(input: AddressFields): {
  errors: AddressCorrectionErrors;
  value: AddressValueFields;
} {
  const errors: AddressCorrectionErrors = {};

  const line1 = input.line1?.trim() ?? "";
  const line2 = input.line2?.trim() ?? "";
  const city = input.city?.trim() ?? "";
  const state = (input.state?.trim() ?? "").toUpperCase();
  const postalCode = input.postalCode?.trim() ?? "";
  const notes = input.notes?.trim() ?? "";

  if (!line1) errors.line1 = "Enter the street address.";
  else if (line1.length > MAX_LINE) {
    errors.line1 = `Please keep this under ${MAX_LINE} characters.`;
  }

  if (line2.length > MAX_LINE) {
    errors.line2 = `Please keep this under ${MAX_LINE} characters.`;
  }

  if (!city) errors.city = "Enter the city or town.";
  else if (city.length > MAX_CITY) {
    errors.city = `Please keep this under ${MAX_CITY} characters.`;
  }

  /*
   * Two letters, matching PatientAddress.Create — which throws — and the char(2) column
   * behind it. Uppercased rather than rejected for case, because "md" is what a thumb
   * types on a phone and the aggregate uppercases too.
   */
  if (!state) errors.state = "Enter the two-letter state code.";
  else if (state.length !== 2) errors.state = "Use the two-letter state code, like MD.";

  if (!postalCode) errors.postalCode = "Enter the ZIP code.";
  else if (postalCode.length > MAX_POSTAL || !ZIP_SHAPE.test(postalCode)) {
    errors.postalCode = "That does not look like a ZIP code.";
  }

  if (notes.length > MAX_NOTES) {
    errors.notes = `Please keep this under ${MAX_NOTES} characters.`;
  }

  return {
    errors,
    value: {
      line1,
      line2: line2 || null,
      city,
      state,
      postalCode,
      notes: notes || null,
    },
  };
}

export function validateNewAddress(input: NewAddressInput): {
  errors: NewAddressErrors;
  value: NewAddressValue;
} {
  const { errors: fieldErrors, value } = validateFields(input);
  const errors: NewAddressErrors = { ...fieldErrors };

  const addressType = input.addressType?.trim() ?? "";
  const effectiveFrom = input.effectiveFrom?.trim() ?? "";

  /*
   * The type decides what supersedes what — a new billing address must not close a session
   * address — so an unrecognised one cannot be allowed through to the enum.
   */
  if (!ADDRESS_TYPES.some((t) => t.value === addressType)) {
    errors.addressType = "Choose whether this is the session or the billing address.";
  }

  /*
   * A move has a date, because the date is what makes the previous address historical
   * rather than wrong. Parsed as UTC so a browser west of Greenwich cannot shift it a day.
   */
  if (!effectiveFrom) {
    errors.effectiveFrom = "Pick the date they moved in.";
  } else if (Number.isNaN(new Date(`${effectiveFrom}T00:00:00Z`).getTime())) {
    errors.effectiveFrom = "That date does not look right.";
  }

  return { errors, value: { ...value, addressType, effectiveFrom } };
}

export function validateAddressCorrection(input: AddressCorrectionInput): {
  errors: AddressCorrectionErrors;
  value: AddressCorrectionValue;
} {
  return validateFields(input);
}
