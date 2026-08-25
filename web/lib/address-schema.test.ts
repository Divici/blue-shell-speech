import { describe, it, expect } from "vitest";
import {
  validateNewAddress,
  validateAddressCorrection,
  ADDRESS_TYPES,
  type NewAddressInput,
  type AddressCorrectionInput,
} from "./address-schema";

/**
 * Address validation for the BFF.
 *
 * Two operations, deliberately not one. Recording a MOVE closes the previous address and
 * keeps it, because a note describing a visit last spring refers to where the family lived
 * then. Fixing a TYPO changes one row in place — the family never lived at the mistyped
 * address, so there is nothing to preserve.
 *
 * Every address here is synthetic and Maryland-shaped. Michelle's own address is not in
 * this repo and never will be (CLAUDE.md non-negotiable #7).
 */

const NEW: NewAddressInput = {
  line1: "14 Elm Street",
  line2: "",
  city: "Towson",
  state: "md",
  postalCode: "21204",
  addressType: "Session",
  notes: "Gate code 4821",
  effectiveFrom: "2026-08-25",
};

function newAddress(overrides: Partial<NewAddressInput> = {}): NewAddressInput {
  return { ...NEW, ...overrides };
}

/**
 * The same address as a CORRECTION — which is to say, without the two fields that make a
 * move a move. Written out rather than destructured off NEW so that adding a field to
 * NewAddressInput does not silently start leaking it into the correction fixture.
 */
function correction(
  overrides: Partial<AddressCorrectionInput> = {},
): AddressCorrectionInput {
  return {
    line1: NEW.line1,
    line2: NEW.line2,
    city: NEW.city,
    state: NEW.state,
    postalCode: NEW.postalCode,
    notes: NEW.notes,
    ...overrides,
  };
}

describe("validateNewAddress", () => {
  it("accepts a Maryland session address and uppercases the state", () => {
    const { errors, value } = validateNewAddress(newAddress());

    expect(errors).toEqual({});
    expect(value).toEqual({
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
   * Mirrors PatientAddress.Create, which throws on anything but two letters, and the
   * char(2) column behind it. Caught here so the clinician reads a sentence rather than a
   * 400 from a serialiser.
   *
   * Control: validateNewAddress — the two-letter state check.
   * Deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("rejects a spelled-out state", () => {
    const { errors } = validateNewAddress(newAddress({ state: "Maryland" }));

    expect(errors.state).toBeTruthy();
  });

  /**
   * Control: validateNewAddress — the ZIP_SHAPE test.
   * Deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("rejects a postal code that is not a US ZIP", () => {
    expect(validateNewAddress(newAddress({ postalCode: "2120" })).errors.postalCode)
      .toBeTruthy();
    expect(validateNewAddress(newAddress({ postalCode: "TW9 1AA" })).errors.postalCode)
      .toBeTruthy();
  });

  it("accepts a ZIP+4", () => {
    const { errors, value } = validateNewAddress(newAddress({ postalCode: "21204-1234" }));

    expect(errors).toEqual({});
    expect(value.postalCode).toBe("21204-1234");
  });

  it.each(["line1", "city", "state", "postalCode"] as const)("requires %s", (field) => {
    const { errors } = validateNewAddress(newAddress({ [field]: "   " }));

    expect(errors[field]).toBeTruthy();
  });

  /**
   * The type decides what supersedes what — a new billing address must not close a session
   * address — so an unrecognised one cannot be allowed through to the enum.
   *
   * Control: validateNewAddress — the ADDRESS_TYPES membership check.
   * Deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("rejects an address type that is not one of the two", () => {
    const { errors } = validateNewAddress(newAddress({ addressType: "Mailing" }));

    expect(errors.addressType).toBeTruthy();
  });

  it("offers exactly the two types the domain has", () => {
    expect(ADDRESS_TYPES.map((t) => t.value)).toEqual(["Session", "Billing"]);
  });

  /**
   * A move has a date, because the date is what makes the previous address historical
   * rather than wrong.
   *
   * The assertion is on the WORDING, not on truthiness, and that is not decoration.
   * Deleting the empty branch left this test green: `new Date("T00:00:00Z")` is an Invalid
   * Date, so the malformed-date check below caught the blank field too and answered "that
   * date does not look right" — about a field nobody had filled in. The second control was
   * covering for the first, and the test could not see it (D066's shape, found by running
   * the deletion rather than by reading the assertion).
   *
   * Control: validateNewAddress — the `if (!effectiveFrom)` branch.
   * Deleted → red, "AssertionError: expected 'That date does not look right.' to match
   * /pick the date/i".
   */
  it("asks for the date the family moved in rather than calling a blank field malformed", () => {
    const { errors } = validateNewAddress(newAddress({ effectiveFrom: "" }));

    expect(errors.effectiveFrom).toMatch(/pick the date/i);
  });

  it("rejects a date that is not a date", () => {
    const { errors } = validateNewAddress(newAddress({ effectiveFrom: "the 4th" }));

    expect(errors.effectiveFrom).toBeTruthy();
  });
});

describe("validateAddressCorrection", () => {
  it("accepts a correction and keeps the same shape minus type and date", () => {
    const { errors, value } = validateAddressCorrection(correction());

    expect(errors).toEqual({});
    expect(value).toEqual({
      line1: "14 Elm Street",
      line2: null,
      city: "Towson",
      state: "MD",
      postalCode: "21204",
      notes: "Gate code 4821",
    });
  });

  /**
   * A CORRECTION CANNOT MOVE THE TYPE OR THE DATES.
   *
   * The type decides what supersedes what and the dates decide which address a past visit
   * happened at. Neither is a typo anyone is fixing, and a correction that could change
   * them would rewrite history under a note that already refers to it — or leave two
   * current session addresses. The value has no such keys to send, which is the guard: it
   * is not a field the caller is trusted to leave alone, it is a field that does not exist.
   *
   * Control: the AddressCorrectionValue shape in address-schema.ts — the absence of
   * addressType and effectiveFrom. Added back as passthrough fields → red,
   * "AssertionError: expected { line1: '14 Elm Street', …(7) } to not have property
   * "addressType"".
   */
  it("has no way to send an address type or an effective date", () => {
    const { value } = validateAddressCorrection({
      ...correction(),
      // A hand-crafted post trying to smuggle them through.
      addressType: "Billing",
      effectiveFrom: "2020-01-01",
    } as AddressCorrectionInput);

    expect(value).not.toHaveProperty("addressType");
    expect(value).not.toHaveProperty("effectiveFrom");
  });

  it("applies the same field rules as a new address", () => {
    expect(validateAddressCorrection(correction({ state: "Maryland" })).errors.state)
      .toBeTruthy();
    expect(validateAddressCorrection(correction({ line1: " " })).errors.line1)
      .toBeTruthy();
    expect(validateAddressCorrection(correction({ postalCode: "abcde" })).errors.postalCode)
      .toBeTruthy();
  });
});
