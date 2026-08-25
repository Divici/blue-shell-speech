import { describe, it, expect } from "vitest";
import {
  validateConversion,
  validateNewPatient,
  type NewPatientInput,
} from "./patient-schema";

const TODAY = new Date("2026-08-24T12:00:00Z");

const valid: NewPatientInput = {
  firstName: "Maya",
  lastName: "Reyes",
  dateOfBirth: "2024-02-24",
  clinicalSummary: "Expressive language delay.",
};

describe("validateNewPatient", () => {
  it("accepts a complete record", () => {
    expect(validateNewPatient(valid, TODAY).errors).toEqual({});
  });

  it("trims names and normalises an empty summary to null", () => {
    const { value } = validateNewPatient(
      { ...valid, firstName: "  Maya  ", clinicalSummary: "   " },
      TODAY,
    );
    expect(value.firstName).toBe("Maya");
    expect(value.clinicalSummary).toBeNull();
  });

  it("requires both names", () => {
    const { errors } = validateNewPatient(
      { ...valid, firstName: " ", lastName: "" },
      TODAY,
    );
    expect(errors.firstName).toBeDefined();
    expect(errors.lastName).toBeDefined();
  });

  it("requires a date of birth", () => {
    expect(validateNewPatient({ ...valid, dateOfBirth: "" }, TODAY).errors.dateOfBirth)
      .toBeDefined();
  });

  it("rejects a future date of birth", () => {
    expect(
      validateNewPatient({ ...valid, dateOfBirth: "2026-08-25" }, TODAY).errors.dateOfBirth,
    ).toBeDefined();
  });

  it("accepts a birth date of today", () => {
    expect(
      validateNewPatient({ ...valid, dateOfBirth: "2026-08-24" }, TODAY).errors.dateOfBirth,
    ).toBeUndefined();
  });

  /**
   * A mistyped year would otherwise sit in the record and distort every age calculation —
   * and age in months drives early-intervention eligibility.
   */
  it("flags an implausible year as a likely typo, without calling it invalid", () => {
    const { errors } = validateNewPatient({ ...valid, dateOfBirth: "1990-02-24" }, TODAY);

    expect(errors.dateOfBirth).toMatch(/typo/i);
    expect(errors.dateOfBirth).not.toMatch(/invalid/i);
  });

  it("rejects a malformed date rather than coercing it", () => {
    expect(
      validateNewPatient({ ...valid, dateOfBirth: "not-a-date" }, TODAY).errors.dateOfBirth,
    ).toBeDefined();
  });

  it("caps the clinical summary", () => {
    expect(
      validateNewPatient({ ...valid, clinicalSummary: "x".repeat(4001) }, TODAY)
        .errors.clinicalSummary,
    ).toBeDefined();
  });
});

/**
 * Converting an enquiry into a patient.
 *
 * The same two fields the new-patient form asks about the child, minus the ones the
 * enquiry already holds. The date rules are the SAME rules — shared through
 * dateOfBirthError rather than restated — because a birthdate the new-patient form calls a
 * typo cannot be one this form accepts.
 */
describe("validateConversion", () => {
  it("accepts a surname and a plausible date of birth", () => {
    expect(
      validateConversion({ lastName: "Reyes", dateOfBirth: "2024-02-24" }, TODAY).errors,
    ).toEqual({});
  });

  it("trims the surname", () => {
    expect(
      validateConversion({ lastName: "  Reyes  ", dateOfBirth: "2024-02-24" }, TODAY)
        .value.lastName,
    ).toBe("Reyes");
  });

  it("asks for a surname, which the public form never collected", () => {
    expect(
      validateConversion({ lastName: "   ", dateOfBirth: "2024-02-24" }, TODAY)
        .errors.lastName,
    ).toBeDefined();
  });

  /**
   * The shared date rules, exercised through the second caller.
   *
   * Control: the dateOfBirthError call in validateConversion.
   * Deleted (dateOfBirth never checked) → red on all four cases, "AssertionError:
   * expected undefined to be defined".
   */
  it.each([
    ["", "missing"],
    ["not-a-date", "malformed"],
    ["2030-01-01", "in the future"],
    ["1990-02-24", "an implausible year"],
  ])("refuses %s (%s)", (dateOfBirth) => {
    expect(
      validateConversion({ lastName: "Reyes", dateOfBirth }, TODAY).errors.dateOfBirth,
    ).toBeDefined();
  });

  /**
   * The two callers give the same answer to the same date.
   *
   * They are separate functions and they must not drift: the day they disagree, a
   * conversion writes a birthdate the ordinary form would have questioned, into the field
   * every early-intervention decision hangs on.
   */
  it("agrees with validateNewPatient about every date", () => {
    for (const dateOfBirth of ["", "not-a-date", "2030-01-01", "1990-02-24", "2024-02-24"]) {
      expect(validateConversion({ lastName: "Reyes", dateOfBirth }, TODAY).errors.dateOfBirth)
        .toBe(validateNewPatient({ ...valid, dateOfBirth }, TODAY).errors.dateOfBirth);
    }
  });
});
