import { describe, it, expect, vi, beforeEach } from "vitest";
import { RATE_LIMITED_MESSAGE } from "./state";

/**
 * What a person is told when the API refused to evaluate their credential.
 *
 * THREE STEPS, THREE CALL SITES, AND THAT IS THE POINT OF TESTING ALL OF THEM. Password,
 * TOTP and recovery-code submissions all reach a rate-limited `/auth` route, so a fix that
 * landed on `signIn` and missed the other two would tell somebody with a perfectly valid
 * authenticator code that it "was not valid" — which is the sibling-left-open failure
 * ORCHESTRATION.md's fix-round brief describes, and which has happened here four rounds
 * running.
 *
 * The message deliberately carries NO DURATION. The login policies send no `Retry-After`
 * (D098), so there is no number to render and none for an attacker to pace against.
 *
 * SYNTHETIC DATA ONLY.
 */

vi.mock("server-only", () => ({}));

const redirect = vi.fn((path: string) => {
  throw new Error(`REDIRECT:${path}`);
});

vi.mock("next/navigation", () => ({
  redirect: (path: string) => redirect(path),
}));

vi.mock("@/lib/auth/session", () => ({
  createPendingMfa: vi.fn(),
  createSession: vi.fn(),
  destroySession: vi.fn(),
  getPendingMfa: async () => ({ userId: "provider-1" }),
}));

const verifyPassword = vi.fn();
const verifyMfa = vi.fn();
const redeemRecoveryCode = vi.fn();
const completeEnrolment = vi.fn();

class ApiRateLimitedError extends Error {}

vi.mock("@/lib/auth/api-client", () => ({
  ApiRateLimitedError,
  authApi: {
    verifyPassword: (...args: unknown[]) => verifyPassword(...args),
    verifyMfa: (...args: unknown[]) => verifyMfa(...args),
    redeemRecoveryCode: (...args: unknown[]) => redeemRecoveryCode(...args),
    completeEnrolment: (...args: unknown[]) => completeEnrolment(...args),
  },
}));

function formDataFrom(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(values)) data.append(key, value);
  return data;
}

const refused = () => {
  throw new ApiRateLimitedError("rate limited");
};

const broken = () => {
  throw new Error("the API is down");
};

describe("a refusal by the rate limiter", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  /**
   * Control: the `if (error instanceof ApiRateLimitedError)` branch in `signIn`'s catch.
   * Deleted → red, "AssertionError: expected 'We could not sign you in. Please try …' to be
   * 'Too many attempts from here in the la…'". A message that is not wrong, exactly, and
   * tells somebody to do the one thing that will not work.
   */
  it("is explained on the password step", async () => {
    verifyPassword.mockImplementation(refused);

    const { signIn } = await import("./actions");

    const result = await signIn(
      { status: "idle" },
      formDataFrom({ email: "michelle@example.com", password: "correct-horse-battery" }),
    );

    expect(result.status).toBe("error");
    expect(result.message).toBe(RATE_LIMITED_MESSAGE);
  });

  /**
   * Control: the same branch in `verifyMfa`'s catch.
   * Deleted → red, "AssertionError: expected 'We could not verify that code. Please…' to be
   * 'Too many attempts from here in the la…'" — on this test AND on the recovery-code one
   * below, since both calls go through the one catch.
   */
  it("is explained on the authenticator step", async () => {
    verifyMfa.mockImplementation(refused);

    const { verifyMfa: action } = await import("./actions");

    const result = await action({ status: "idle" }, formDataFrom({ code: "123456" }));

    expect(result.message).toBe(RATE_LIMITED_MESSAGE);
  });

  /**
   * Control: the same branch again, reached through the recovery-code path.
   * Deleted → red, "AssertionError: expected 'We could not verify that code. Please…' to be
   * 'Too many attempts from here in the la…'". Same catch, different call, and worth its own
   * case: telling somebody a one-shot recovery code was invalid, when it was never redeemed,
   * would have them burn the next one.
   */
  it("is explained on the recovery-code step", async () => {
    redeemRecoveryCode.mockImplementation(refused);

    const { verifyMfa: action } = await import("./actions");

    const result = await action(
      { status: "idle" },
      formDataFrom({ code: "AAAA-BBBB", mode: "recovery" }),
    );

    expect(result.message).toBe(RATE_LIMITED_MESSAGE);
  });

  /**
   * Control: the same branch in `completeEnrolment`'s catch.
   * Deleted → red, "AssertionError: expected 'We could not confirm that code. Pleas…' to be
   * 'Too many attempts from here in the la…'". This is the step the lockout does not count at
   * all, so the limiter is the only bound on it.
   */
  it("is explained on the enrolment step", async () => {
    completeEnrolment.mockImplementation(refused);

    const { completeEnrolment: action } = await import("./actions");

    const result = await action({ status: "idle" }, formDataFrom({ code: "123456" }));

    expect(result.message).toBe(RATE_LIMITED_MESSAGE);
  });

  /**
   * The generic collapse still holds for everything else, which is the property the new
   * branch must not have eroded: "the service is unavailable" versus "that was wrong" tells
   * an attacker whether their guess was even evaluated.
   */
  it("does not swallow every other failure into the same message", async () => {
    verifyPassword.mockImplementation(broken);

    const { signIn } = await import("./actions");

    const result = await signIn(
      { status: "idle" },
      formDataFrom({ email: "michelle@example.com", password: "correct-horse-battery" }),
    );

    expect(result.message).toBe("We could not sign you in. Please try again.");
  });

  /** No number to render, and none for an attacker to pace against (D098). */
  it("names no duration", () => {
    expect(RATE_LIMITED_MESSAGE).not.toMatch(/\d/);
  });
});
