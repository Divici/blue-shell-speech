import { describe, it, expect, vi, beforeEach } from "vitest";

/**
 * Discarding an empty draft.
 *
 * The one delete in this product, and the escape hatch for a mis-tap on the schedule: an
 * empty draft cannot be signed and cannot be replaced, so before this existed it could
 * only be cleared by writing content onto that child's chart.
 *
 * The API is the control — it refuses anything with content or a signature, and so does
 * the database. These assert the action does not report a delete that never happened, and
 * does not swallow the redirect that follows one.
 */

const api = vi.hoisted(() => {
  class ApiConflictError extends Error {}

  return {
    ApiConflictError,
    discardDraft: vi.fn(),
    // redirect() throws, and modelling it as a throw is what catches a redirect placed
    // inside a try/catch — see D061.
    redirect: vi.fn((url: string) => {
      throw new Error(`NEXT_REDIRECT:${url}`);
    }),
    revalidatePath: vi.fn(),
  };
});

vi.mock("server-only", () => ({}));
vi.mock("next/cache", () => ({ revalidatePath: api.revalidatePath }));
vi.mock("next/navigation", () => ({ redirect: api.redirect }));
vi.mock("@/lib/api/notes", () => ({
  ApiConflictError: api.ApiConflictError,
  notesApi: { discardDraft: api.discardDraft },
}));

import { discardDraft } from "./actions";
import { INITIAL_NOTE_STATE } from "./state";

const NOTE = "8f3c1d2e-0000-4000-8000-000000000002";

function run(publicId?: string) {
  const data = new FormData();
  if (publicId !== undefined) data.append("publicId", publicId);
  return discardDraft(INITIAL_NOTE_STATE, data);
}

describe("discardDraft", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("discards the note and returns to the schedule", async () => {
    api.discardDraft.mockResolvedValue({ publicId: NOTE });

    await expect(run(NOTE)).rejects.toThrow(/NEXT_REDIRECT/);

    expect(api.discardDraft).toHaveBeenCalledWith(NOTE);
    expect(api.redirect).toHaveBeenCalledWith("/today");

    // The visit must read as undocumented again on the way back.
    expect(api.revalidatePath).toHaveBeenCalledWith("/today");
  });

  /**
   * The API refuses a note with anything written in it, and its wording explains the rule.
   * Replacing that with a generic failure would leave the clinician re-tapping a button
   * that is behaving correctly.
   */
  it("surfaces the API's reason for refusing", async () => {
    api.discardDraft.mockRejectedValue(
      new api.ApiConflictError("This note is signed. A signed clinical record is never deleted."),
    );

    const result = await run(NOTE);

    expect(result.status).toBe("error");
    expect(result.message).toMatch(/signed/i);
    expect(api.redirect).not.toHaveBeenCalled();
  });

  /**
   * A 404 arrives as null — a note belonging to another provider and one that does not
   * exist are byte-identical (D052). Either way nothing was deleted, so nothing may be
   * reported as deleted.
   */
  it("does not claim success when the note could not be reached", async () => {
    api.discardDraft.mockResolvedValue(null);

    const result = await run(NOTE);

    expect(result.status).toBe("error");
    expect(api.redirect).not.toHaveBeenCalled();
  });

  it("does not surface a raw transport error", async () => {
    api.discardDraft.mockRejectedValue(new Error("Notes API /notes/x failed with 503"));

    const result = await run(NOTE);

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/503|Notes API/);
    expect(result.message).toBeTruthy();
  });

  it("does not call the API without a note id", async () => {
    const result = await run();

    expect(result.status).toBe("error");
    expect(api.discardDraft).not.toHaveBeenCalled();
  });
});
