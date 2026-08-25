import { describe, it, expect, vi, beforeEach } from "vitest";

/**
 * The three moves an enquiry can make, driven from the detail page.
 *
 * None of these is about rendering, so none is asserted through a browser: what matters is
 * that a refusal keeps the API's own sentence, that an enquiry belonging to somebody else
 * produces a message saying nothing about whether it exists, and that the conversion's
 * redirect sits outside the catch — a redirect signals by throwing, and a catch-all would
 * tell a clinician her child record failed to save when it had.
 */

const api = vi.hoisted(() => {
  class ApiConflictError extends Error {}

  return {
    ApiConflictError,
    markContacted: vi.fn(),
    decline: vi.fn(),
    convert: vi.fn(),
    /*
     * Next's redirect() throws, and modelling it as a throw is load-bearing: it is what
     * makes these tests able to catch a redirect placed inside a try/catch.
     */
    redirect: vi.fn((url: string) => {
      throw new Error(`NEXT_REDIRECT:${url}`);
    }),
    revalidatePath: vi.fn(),
  };
});

vi.mock("server-only", () => ({}));
vi.mock("next/cache", () => ({ revalidatePath: api.revalidatePath }));
vi.mock("next/navigation", () => ({ redirect: api.redirect }));
vi.mock("@/lib/api/errors", () => ({ ApiConflictError: api.ApiConflictError }));
vi.mock("@/lib/api/enquiries", () => ({
  enquiriesApi: {
    markContacted: api.markContacted,
    decline: api.decline,
    convert: api.convert,
  },
}));

import { convertToPatient, declineEnquiry, markContacted } from "./actions";
import {
  ENQUIRY_UNAVAILABLE,
  INITIAL_CONVERT_STATE,
  INITIAL_ENQUIRY_TRANSITION_STATE,
} from "./state";

const ENQUIRY = "8f3c1d2e-0000-4000-8000-0000000000a1";
const PATIENT = "8f3c1d2e-0000-4000-8000-0000000000b1";

function transition(
  action: typeof markContacted,
  // `null` rather than `undefined` for "no id at all": a default parameter fires on
  // undefined, so the omitted-field case would silently become the ordinary one.
  publicId: string | null = ENQUIRY,
) {
  const data = new FormData();
  if (publicId !== null) data.append("publicId", publicId);
  return action(INITIAL_ENQUIRY_TRANSITION_STATE, data);
}

function conversion(fields: Record<string, string> = {}) {
  const data = new FormData();
  data.append("publicId", ENQUIRY);
  data.append("lastName", "Reyes");
  data.append("dateOfBirth", "2024-02-24");
  for (const [name, value] of Object.entries(fields)) data.set(name, value);
  return convertToPatient(INITIAL_CONVERT_STATE, data);
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("markContacted", () => {
  /**
   * Control: the revalidatePath calls in transitionEnquiry.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" to be called with arguments:
   * [ "/enquiries" ]" — the inbox keeps rendering the enquiry as New until something
   * else invalidates it.
   */
  it("records the reply and refreshes both views", async () => {
    api.markContacted.mockResolvedValue({ publicId: ENQUIRY, status: "Contacted" });

    const result = await transition(markContacted);

    expect(api.markContacted).toHaveBeenCalledWith(ENQUIRY);
    expect(result.status).toBe("idle");
    expect(api.revalidatePath).toHaveBeenCalledWith("/enquiries");
    expect(api.revalidatePath).toHaveBeenCalledWith(`/enquiries/${ENQUIRY}`);
  });

  /**
   * An enquiry belonging to another provider and one that does not exist BOTH arrive here
   * as null, because the API answers 404 to each (D052). The action must not invent a
   * distinction the API deliberately refused to make.
   *
   * Control: the `if (!moved)` guard in transitionEnquiry.
   * Deleted → red, "AssertionError: expected 'idle' to be 'error'" — the page reports a
   * state change that never happened.
   */
  it("says nothing about whether an unreachable enquiry exists", async () => {
    api.markContacted.mockResolvedValue(null);

    const result = await transition(markContacted);

    expect(result.status).toBe("error");
    expect(result.message).toBe(ENQUIRY_UNAVAILABLE);
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });

  /**
   * A closed enquiry. The API's sentence explains a rule and says what to do instead, so
   * it is surfaced rather than replaced with "please try again".
   *
   * Control: the `error instanceof ApiConflictError` branch in transitionEnquiry.
   * Deleted → red, "AssertionError: expected 'That enquiry could not be updated. Re…' to
   * be 'This enquiry has already become a pat…'".
   */
  it("passes a refusal through in the API's words", async () => {
    api.markContacted.mockRejectedValue(
      new api.ApiConflictError(
        "This enquiry has already become a patient. Open the patient record instead.",
      ),
    );

    const result = await transition(markContacted);

    expect(result.status).toBe("error");
    expect(result.message).toBe(
      "This enquiry has already become a patient. Open the patient record instead.",
    );
  });

  it("fails safely when the API is unreachable", async () => {
    api.markContacted.mockRejectedValue(new Error("ECONNREFUSED"));

    expect((await transition(markContacted)).message).toBe(ENQUIRY_UNAVAILABLE);
  });

  it("refuses a submission with no enquiry on it", async () => {
    const result = await transition(markContacted, null);

    expect(result.status).toBe("error");
    expect(api.markContacted).not.toHaveBeenCalled();
  });
});

describe("declineEnquiry", () => {
  /**
   * Control: the `enquiriesApi.decline` passed to transitionEnquiry.
   * Replaced with markContacted → red, "AssertionError: expected \"vi.fn()\" to be called
   * with arguments: [ Array(1) ]" on decline.
   */
  it("declines rather than marking contacted", async () => {
    api.decline.mockResolvedValue({ publicId: ENQUIRY, status: "Declined" });

    await transition(declineEnquiry);

    expect(api.decline).toHaveBeenCalledWith(ENQUIRY);
    expect(api.markContacted).not.toHaveBeenCalled();
  });
});

describe("convertToPatient", () => {
  /**
   * The whole point of converting: the clinician lands on the child's new record.
   *
   * Control: the `redirect(\`/patients/${…}\`)` at the end of convertToPatient.
   * Deleted → red, "AssertionError: promise resolved \"{ status: 'idle', errors: {} }\"
   * instead of rejecting" — the form falls through and the clinician is left on an enquiry
   * with no sign of where the patient went.
   */
  it("creates the patient and opens the record", async () => {
    api.convert.mockResolvedValue({
      publicId: ENQUIRY,
      status: "Converted",
      convertedPatientPublicId: PATIENT,
    });

    await expect(conversion()).rejects.toThrow(/NEXT_REDIRECT/);

    expect(api.convert).toHaveBeenCalledWith(ENQUIRY, {
      lastName: "Reyes",
      dateOfBirth: "2024-02-24",
    });
    expect(api.redirect).toHaveBeenCalledWith(`/patients/${PATIENT}`);
    expect(api.revalidatePath).toHaveBeenCalledWith("/patients");
  });

  /**
   * The redirect is OUTSIDE the try/catch, and this is what proves it.
   *
   * redirect() signals by throwing. Called inside a catch-all it is caught as a failure,
   * and a clinician whose patient record was created perfectly well is told it was not —
   * so she taps again, and the second attempt is refused because the enquiry is already
   * converted. The mistake D061 records.
   *
   * Control: the placement of redirect() after the try/catch in convertToPatient.
   * Wrapped in a try/catch → red, "AssertionError: promise resolved \"{ status: 'error',
   * errors: {}, …(2) }\" instead of rejecting" — an error state, on a conversion that
   * succeeded.
   */
  it("lets the redirect signal escape rather than reporting it as a failure", async () => {
    api.convert.mockResolvedValue({
      publicId: ENQUIRY,
      status: "Converted",
      convertedPatientPublicId: PATIENT,
    });

    await expect(conversion()).rejects.toThrow(/NEXT_REDIRECT/);
  });

  /**
   * Control: the validateConversion call in convertToPatient.
   * Deleted → red, "AssertionError: expected \"vi.fn()\" to not be called at all, but
   * actually been called 1 times" — a blank surname reaches the API, whose aggregate
   * refuses it with a message written for a developer.
   */
  it("validates before it writes, and keeps what was typed", async () => {
    const result = await conversion({ lastName: "  " });

    expect(api.convert).not.toHaveBeenCalled();
    expect(result.status).toBe("error");
    expect(result.errors.lastName).toBeDefined();
    expect(result.values?.dateOfBirth).toBe("2024-02-24");
  });

  it("surfaces a refusal in the API's words", async () => {
    api.convert.mockRejectedValue(
      new api.ApiConflictError("This enquiry was declined and is kept as it was."),
    );

    const result = await conversion();

    expect(result.message).toBe("This enquiry was declined and is kept as it was.");
    expect(result.values?.lastName).toBe("Reyes");
    expect(api.redirect).not.toHaveBeenCalled();
  });

  /**
   * A conversion that came back without a patient is not a conversion.
   *
   * Redirecting to `/patients/null` would be the visible version; the invisible one is
   * telling somebody a child's record exists when the API never said so.
   *
   * Control: the `if (!converted?.convertedPatientPublicId)` guard in convertToPatient.
   * Deleted (leaving only the `!converted` half) → red, "Error: NEXT_REDIRECT:
   * /patients/null" — the clinician is sent to a patient record that does not exist.
   */
  it("does not announce a patient the API did not name", async () => {
    api.convert.mockResolvedValue({
      publicId: ENQUIRY,
      status: "Converted",
      convertedPatientPublicId: null,
    });

    const result = await conversion();

    expect(result.status).toBe("error");
    expect(api.redirect).not.toHaveBeenCalled();
  });

  it("says nothing about whether an unreachable enquiry exists", async () => {
    api.convert.mockResolvedValue(null);

    const result = await conversion();

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });
});
