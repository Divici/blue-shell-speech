import { describe, it, expect } from "vitest";
import {
  validateConsultation,
  isLikelyBot,
  MAX_CHILD_AGE_MONTHS,
  type ConsultationInput,
} from "./consultation-schema";

const valid: ConsultationInput = {
  parentName: "Jordan Reyes",
  email: "jordan@example.com",
  phone: "410-555-0142",
  childFirstName: "Maya",
  childAgeMonths: "30",
  concerns: "She has a handful of words but isn't putting two together yet.",
  preferredContact: "Either",
};

describe("validateConsultation", () => {
  it("accepts a complete submission", () => {
    const { errors } = validateConsultation(valid);
    expect(errors).toEqual({});
  });

  it("trims values so a trailing space cannot create a duplicate enquiry", () => {
    const { value } = validateConsultation({
      ...valid,
      parentName: "  Jordan Reyes  ",
      email: " jordan@example.com ",
    });
    expect(value.parentName).toBe("Jordan Reyes");
    expect(value.email).toBe("jordan@example.com");
  });

  it("requires a parent name, child name, concerns, and contact preference", () => {
    const { errors } = validateConsultation({
      ...valid,
      parentName: "  ",
      childFirstName: "",
      concerns: "",
      preferredContact: "",
    });
    expect(errors.parentName).toBeDefined();
    expect(errors.childFirstName).toBeDefined();
    expect(errors.concerns).toBeDefined();
    expect(errors.preferredContact).toBeDefined();
  });

  it("rejects a malformed email", () => {
    expect(validateConsultation({ ...valid, email: "jordan@" }).errors.email).toBeDefined();
    expect(validateConsultation({ ...valid, email: "jordan" }).errors.email).toBeDefined();
  });

  it("only requires a phone number when the parent wants to be phoned", () => {
    expect(
      validateConsultation({ ...valid, phone: "", preferredContact: "Email" }).errors.phone,
    ).toBeUndefined();

    expect(
      validateConsultation({ ...valid, phone: "", preferredContact: "Phone" }).errors.phone,
    ).toBeDefined();
  });

  it("rejects an unrecognised contact preference rather than trusting the select", () => {
    const { errors } = validateConsultation({ ...valid, preferredContact: "Carrier pigeon" });
    expect(errors.preferredContact).toBeDefined();
  });

  describe("child age", () => {
    it("accepts newborns", () => {
      expect(validateConsultation({ ...valid, childAgeMonths: "0" }).errors.childAgeMonths)
        .toBeUndefined();
    });

    it("accepts the top of the served range", () => {
      expect(
        validateConsultation({ ...valid, childAgeMonths: String(MAX_CHILD_AGE_MONTHS) })
          .errors.childAgeMonths,
      ).toBeUndefined();
    });

    it("rejects negative and non-numeric ages", () => {
      expect(validateConsultation({ ...valid, childAgeMonths: "-3" }).errors.childAgeMonths)
        .toBeDefined();
      expect(validateConsultation({ ...valid, childAgeMonths: "two" }).errors.childAgeMonths)
        .toBeDefined();
    });

    /**
     * Out of range is not the parent's mistake. The message must not read as a rejection
     * — a parent whose child is 6 should still be told where to go next.
     */
    it("explains rather than scolds when the child is older than the practice serves", () => {
      const { errors } = validateConsultation({
        ...valid,
        childAgeMonths: String(MAX_CHILD_AGE_MONTHS + 1),
      });
      expect(errors.childAgeMonths).toMatch(/birth to 5/i);
      expect(errors.childAgeMonths).not.toMatch(/invalid|error/i);
    });
  });

  it("caps free text so a paste cannot blow up the record", () => {
    const { errors } = validateConsultation({ ...valid, concerns: "x".repeat(2001) });
    expect(errors.concerns).toBeDefined();
  });
});

describe("isLikelyBot", () => {
  it("is false for a real submission", () => {
    expect(isLikelyBot(valid)).toBe(false);
  });

  it("is true when the honeypot is filled", () => {
    expect(isLikelyBot({ ...valid, website: "http://spam.example" })).toBe(true);
  });

  it("ignores whitespace in the honeypot", () => {
    expect(isLikelyBot({ ...valid, website: "   " })).toBe(false);
  });
});
