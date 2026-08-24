import { describe, it, expect } from "vitest";
import { validateNewPatient, type NewPatientInput } from "./patient-schema";

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
