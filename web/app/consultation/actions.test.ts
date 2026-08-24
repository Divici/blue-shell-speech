import { describe, it, expect, vi, beforeEach } from "vitest";

/**
 * The action's echo behaviour, tested directly.
 *
 * This is the logic behind "a validation failure must not erase what the parent typed".
 * It is asserted here rather than only through a browser, because the browser test proved
 * to depend on Server Action hydration timing that differs between engines — and the rule
 * itself has nothing to do with any of that.
 */

vi.mock("server-only", () => ({}));
vi.mock("next/headers", () => ({
  headers: async () => new Map([["x-forwarded-for", "203.0.113.7"]]),
}));

function formDataFrom(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(values)) data.append(key, value);
  return data;
}

const CONCERNS =
  "She has around ten words but is not combining them, and she gets frustrated.";

const COMPLETE = {
  parentName: "Jordan Reyes",
  email: "jordan@example.com",
  phone: "410-555-0142",
  childFirstName: "Maya",
  childAgeMonths: "30",
  concerns: CONCERNS,
  preferredContact: "Either",
};

describe("submitConsultation", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  it("echoes every field back when validation fails", async () => {
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom({ ...COMPLETE, email: "not-an-email" }),
    );

    expect(result.status).toBe("error");
    expect(result.errors.email).toBeDefined();

    // The long free-text field is the one that matters most — it is the hardest to
    // retype and it is a parent describing their child's difficulties.
    expect(result.values?.concerns).toBe(CONCERNS);
    expect(result.values?.parentName).toBe("Jordan Reyes");
    expect(result.values?.childFirstName).toBe("Maya");
    expect(result.values?.childAgeMonths).toBe("30");
    expect(result.values?.preferredContact).toBe("Either");
  });

  /**
   * A parse failure yields -1 internally. Echoing that back would show "-1" to someone
   * who typed "two" — worse than showing them what they wrote.
   */
  it("echoes the raw age text, not the parsed number", async () => {
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom({ ...COMPLETE, childAgeMonths: "two" }),
    );

    expect(result.errors.childAgeMonths).toBeDefined();
    expect(result.values?.childAgeMonths).toBe("two");
  });

  /** Echoing the honeypot back would refill the trap and defeat it on resubmission. */
  it("never echoes the honeypot", async () => {
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom({ ...COMPLETE, email: "not-an-email", website: "http://spam.example" }),
    );

    // A filled honeypot is silently accepted, so this returns success and stores nothing.
    expect(result.status).toBe("success");
    expect(result.values).toBeUndefined();
  });

  it("returns no echoed values on success", async () => {
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(result.status).toBe("success");
    expect(result.values).toBeUndefined();
  });

  it("rate-limits repeated submissions from the same caller", async () => {
    const { submitConsultation } = await import("./actions");

    for (let i = 0; i < 5; i++) {
      await submitConsultation({ status: "idle", errors: {} }, formDataFrom(COMPLETE));
    }

    const blocked = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(blocked.status).toBe("error");
    expect(blocked.message).toMatch(/wait/i);
  });
});
