import { describe, it, expect, vi, beforeEach } from "vitest";
import { hashClientId, clientIdentifier } from "@/lib/rate-limit";

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

/**
 * The API is mocked, not the action.
 *
 * What is under test is the BFF s own decisions — when it calls, when it refuses to, and
 * what it confirms to the parent afterwards. The API s behaviour has its own suite against
 * a real database (Practice.Api.Tests.ConsultationIntakeTests); duplicating it here would
 * assert the mock.
 */
const submit = vi.fn();
vi.mock("@/lib/api/consultations", () => ({
  consultationsApi: { submit: (...args: unknown[]) => submit(...args) },
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
    submit.mockReset();
    submit.mockResolvedValue({
      stored: true,
      publicId: "3f7c9f2e-8a41-4c3d-9f0b-2d6c8e1a5b47",
    });
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
      formDataFrom({
        ...COMPLETE,
        email: "not-an-email",
        website: "http://spam.example",
      }),
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
      await submitConsultation(
        { status: "idle", errors: {} },
        formDataFrom(COMPLETE),
      );
    }

    const blocked = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(blocked.status).toBe("error");
    expect(blocked.message).toMatch(/wait/i);
  });
});

/**
 * PERSISTENCE — the criterion slice 1 could not meet, and the controls around it.
 *
 * The form used to validate, confirm, and store nothing. That gap was recorded rather than
 * hidden (`docs/SLICE_1_VERIFICATION.md`), and closing it changes what a confirmation
 * MEANS: "thank you, your request is on its way" is now a claim about a row, so every path
 * that does not produce one has to stop saying it.
 */
describe("submitConsultation · persistence", () => {
  beforeEach(() => {
    vi.resetModules();
    submit.mockReset();
    submit.mockResolvedValue({
      stored: true,
      publicId: "3f7c9f2e-8a41-4c3d-9f0b-2d6c8e1a5b47",
    });
  });

  /**
   * Control: the `consultationsApi.submit(...)` call in submitConsultation.
   * Deleted → red on the first assertion, "AssertionError: expected \"vi.fn()\" to be
   * called 1 times, but got 0 times".
   */
  it("sends the enquiry to the API and confirms only then", async () => {
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(submit).toHaveBeenCalledTimes(1);
    expect(result.status).toBe("success");
  });

  /**
   * THE PARENT IS NOT THANKED FOR SOMETHING THAT DID NOT HAPPEN.
   *
   * A family told "we'll be in touch" whose enquiry was never recorded does not follow up,
   * and nobody ever finds out — which is worse than an error message by a wide margin. The
   * error keeps everything they typed and points at the phone.
   *
   * Control: the `!outcome.stored` branch in submitConsultation.
   * Deleted → red, "AssertionError: expected 'success' to be 'error'".
   */
  it("does not confirm when the enquiry could not be stored", async () => {
    submit.mockResolvedValue({ stored: false });
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(result.status).toBe("error");
    expect(result.message).toMatch(/call/i);

    // Everything they wrote survives, including the long free-text field.
    expect(result.values?.concerns).toBe(CONCERNS);
    expect(result.values?.childFirstName).toBe("Maya");
  });

  /**
   * An unreachable API is an error the parent can act on, not a crash.
   *
   * Control: the try/catch around the API call in submitConsultation.
   * Deleted → red, the action rejects rather than answering: "Error: the API is
   * unreachable".
   */
  it("does not confirm when the API call throws", async () => {
    submit.mockRejectedValue(new Error("the API is unreachable"));
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(result.status).toBe("error");
    expect(result.message).toMatch(/call/i);
  });

  /**
   * The hashed source, and never the address.
   *
   * The SAME value the limiter keys on — one derivation, two uses. A second hashing scheme
   * would produce a column that correlates with nothing the limiter ever saw.
   *
   * Control: the `hashClientId(...)` in the action's clientKey().
   * Deleted, i.e. passing the address straight through → red, "AssertionError: expected
   * '203.0.113.7' to match /^[0-9a-f]{64}$/".
   */
  it("sends the hashed source, not the address", async () => {
    const { submitConsultation } = await import("./actions");

    await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    const sent = submit.mock.calls[0]![0] as { sourceIpHash: string };
    expect(sent.sourceIpHash).toMatch(/^[0-9a-f]{64}$/);
    expect(sent.sourceIpHash).not.toContain("203.0.113.7");
    expect(sent.sourceIpHash).toBe(
      await hashClientId(clientIdentifier("203.0.113.7")),
    );
  });

  /**
   * The parsed number, not the raw text: the API's column is a smallint, and JSON has no
   * coercion to save a string that got there by accident.
   *
   * Control: the `childAgeMonths: value.childAgeMonths` in the submit payload — the
   * VALIDATED value rather than the raw input.
   * Swapped for `input.childAgeMonths` → red, "AssertionError: expected '30' to be 30".
   */
  it("sends the age as a number", async () => {
    const { submitConsultation } = await import("./actions");

    await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(
      (submit.mock.calls[0]![0] as { childAgeMonths: number }).childAgeMonths,
    ).toBe(30);
  });

  /**
   * Control: the early return on `Object.keys(errors).length > 0` in submitConsultation.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" not to be called at all, but
   * actually been called 1 times".
   */
  it("never reaches the API when validation fails", async () => {
    const { submitConsultation } = await import("./actions");

    await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom({ ...COMPLETE, email: "not-an-email" }),
    );

    expect(submit).not.toHaveBeenCalled();
  });

  /**
   * A trapped bot is told it succeeded and stores nothing — the silence is the trap.
   *
   * Control: the `isLikelyBot(input)` branch in submitConsultation.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" not to be called at all, but
   * actually been called 1 times".
   */
  it("never reaches the API when the honeypot is filled", async () => {
    const { submitConsultation } = await import("./actions");

    const result = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom({ ...COMPLETE, website: "http://spam.example" }),
    );

    expect(result.status).toBe("success");
    expect(submit).not.toHaveBeenCalled();
  });

  /**
   * THE RATE LIMIT PROTECTS THE WRITE, NOT JUST THE ANSWER.
   *
   * A limiter that returns a polite message and still posts to the API would leave the
   * whole cost — a woken container, a transaction, a row, an audit entry — on the table,
   * which is the entire threat `THREAT_MODEL.md` boundary 1 names for this form. The
   * throttled request must make NO call at all.
   *
   * Control: the `if (!allowed) return` early exit in submitConsultation.
   * Deleted → red, "AssertionError: expected 'success' to be 'error'" — the sixth
   * submission is answered as though it had been accepted, and the count below it never
   * gets a chance to disagree.
   */
  it("makes no API call once the caller is rate-limited", async () => {
    const { submitConsultation } = await import("./actions");

    for (let i = 0; i < 5; i++) {
      await submitConsultation(
        { status: "idle", errors: {} },
        formDataFrom(COMPLETE),
      );
    }
    expect(submit).toHaveBeenCalledTimes(5);

    const blocked = await submitConsultation(
      { status: "idle", errors: {} },
      formDataFrom(COMPLETE),
    );

    expect(blocked.status).toBe("error");
    expect(submit).toHaveBeenCalledTimes(5);
  });
});
