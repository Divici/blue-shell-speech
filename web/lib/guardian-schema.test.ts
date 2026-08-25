import { describe, it, expect } from "vitest";
import {
  recordsReleaseState,
  validateGuardian,
  LEGAL_AUTHORITY_UNSTATED,
  type GuardianInput,
} from "./guardian-schema";

/**
 * Guardian validation for the BFF.
 *
 * One rule carries the weight: legal authority is its OWN answer. It is not the primary
 * contact flag, it is not implied by one, and it has no default — a guardian saved without
 * an answer is refused rather than recorded as "no". Everything else here is ordinary
 * field validation that keeps a rejection readable instead of arriving as a 400 from a
 * serialiser.
 *
 * Every fixture is synthetic. The addresses and numbers are Maryland-shaped and belong to
 * nobody (CLAUDE.md non-negotiables #1 and #7).
 */

const VALID: GuardianInput = {
  firstName: "Jordan",
  lastName: "Reyes",
  relationship: "Mother",
  phone: "410-555-0142",
  email: "",
  isPrimaryContact: true,
  hasLegalAuthority: "yes",
};

function input(overrides: Partial<GuardianInput> = {}): GuardianInput {
  return { ...VALID, ...overrides };
}

describe("validateGuardian", () => {
  it("accepts a complete guardian and normalises the optional fields to null", () => {
    const { errors, value } = validateGuardian(input());

    expect(errors).toEqual({});
    expect(value).toEqual({
      firstName: "Jordan",
      lastName: "Reyes",
      relationship: "Mother",
      phone: "410-555-0142",
      email: null,
      isPrimaryContact: true,
      hasLegalAuthority: true,
    });
  });

  /**
   * THE RULE THIS MODULE EXISTS FOR.
   *
   * The column is a bit and cannot hold "nobody said". An unanswered control that
   * submitted `false` would write down a decision that this parent may not have their
   * child's file — and leave it indistinguishable from someone deciding that. So the form
   * asks, and silence is refused rather than resolved.
   *
   * Control: validateGuardian — the answer check that assigns LEGAL_AUTHORITY_UNSTATED,
   * together with the conditional spread that withholds the value.
   * Deleted, i.e. reverted to a plain `authority === "yes"` → red on the first assertion,
   * "AssertionError: expected undefined to be 'Say whether this person may receive t…'" —
   * silence had been accepted and written down as a "no".
   */
  it("refuses a guardian whose legal authority nobody answered", () => {
    const { errors, value } = validateGuardian(input({ hasLegalAuthority: "" }));

    expect(errors.hasLegalAuthority).toBe(LEGAL_AUTHORITY_UNSTATED);
    expect(value.hasLegalAuthority).toBeUndefined();
  });

  it("reads legal authority from its own answer, not from the primary contact flag", () => {
    const stepparent = validateGuardian(
      input({ isPrimaryContact: true, hasLegalAuthority: "no" }),
    );
    const nonCustodial = validateGuardian(
      input({ isPrimaryContact: false, hasLegalAuthority: "yes", phone: "" , email: ""}),
    );

    expect(stepparent.value.isPrimaryContact).toBe(true);
    expect(stepparent.value.hasLegalAuthority).toBe(false);

    expect(nonCustodial.value.isPrimaryContact).toBe(false);
    expect(nonCustodial.value.hasLegalAuthority).toBe(true);
  });

  it("rejects an answer that is neither yes nor no", () => {
    const { errors } = validateGuardian(input({ hasLegalAuthority: "maybe" }));

    expect(errors.hasLegalAuthority).toBe(LEGAL_AUTHORITY_UNSTATED);
  });

  it.each(["firstName", "lastName", "relationship"] as const)("requires %s", (field) => {
    const { errors } = validateGuardian(input({ [field]: "   " }));

    expect(errors[field]).toBeTruthy();
  });

  /**
   * Mirrors Guardian.Create and Guardian.UpdateContact, both of which throw. A primary
   * contact with no phone and no email is a record that looks complete and is useless the
   * first time a session has to move.
   *
   * Control: validateGuardian — the `isPrimaryContact && !phone && !email` branch.
   * Deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("refuses a primary contact with no way to be contacted", () => {
    const { errors } = validateGuardian(
      input({ isPrimaryContact: true, phone: "  ", email: "" }),
    );

    expect(errors.phone).toBeTruthy();
  });

  it("allows a guardian who is not the primary contact to have no contact details", () => {
    const { errors } = validateGuardian(
      input({ isPrimaryContact: false, phone: "", email: "" }),
    );

    expect(errors).toEqual({});
  });

  /**
   * Control: validateGuardian — the EMAIL_SHAPE test.
   * Deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("rejects an email address that could not be one", () => {
    const { errors } = validateGuardian(input({ email: "jordan at example" }));

    expect(errors.email).toBeTruthy();
  });

  it("accepts an email address that could be one", () => {
    const { errors, value } = validateGuardian(input({ email: "jordan@example.com" }));

    expect(errors).toEqual({});
    expect(value.email).toBe("jordan@example.com");
  });

  /**
   * Column widths, so a rejection happens here rather than as a silent truncation
   * downstream.
   *
   * Control: validateGuardian — the MAX_NAME length check on firstName.
   * Deleted → red, "AssertionError: expected undefined to be truthy".
   */
  it("rejects a name longer than the column", () => {
    const { errors } = validateGuardian(input({ firstName: "J".repeat(101) }));

    expect(errors.firstName).toBeTruthy();
  });
});

describe("recordsReleaseState", () => {
  /**
   * The three states a patient record can be in, named once so the page renders on the
   * same predicate everywhere rather than on a locally reinvented condition (D062).
   *
   * "None authorised" is a real state and NOT an error: a family whose custody paperwork
   * has not arrived genuinely has nobody who may receive the file yet. The page says so
   * rather than inventing an answer.
   *
   * Control: recordsReleaseState — the `guardians.length === 0` branch.
   * Deleted → red, "AssertionError: expected 'none-authorised' to be 'no-guardians'".
   */
  it("distinguishes no guardians from guardians with no authority", () => {
    expect(recordsReleaseState([])).toBe("no-guardians");

    expect(
      recordsReleaseState([{ hasLegalAuthority: false }, { hasLegalAuthority: false }]),
    ).toBe("none-authorised");

    expect(
      recordsReleaseState([{ hasLegalAuthority: false }, { hasLegalAuthority: true }]),
    ).toBe("authorised");
  });

  /**
   * Control: recordsReleaseState — the `.some(g => g.hasLegalAuthority)` test.
   * Replaced with `.every(...)` → red on the mixed case, "AssertionError: expected
   * 'none-authorised' to be 'authorised'".
   */
  it("counts one authorised guardian as enough, whoever the primary contact is", () => {
    expect(recordsReleaseState([{ hasLegalAuthority: true }])).toBe("authorised");
    expect(
      recordsReleaseState([{ hasLegalAuthority: true }, { hasLegalAuthority: false }]),
    ).toBe("authorised");
  });
});
