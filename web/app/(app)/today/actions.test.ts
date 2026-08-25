import { describe, it, expect, vi, beforeEach } from "vitest";

/**
 * Starting a note from the day view.
 *
 * The rules being asserted are not about rendering, so they are asserted here rather than
 * through a browser: a second note is never created, a redirect actually happens (and
 * nothing runs after it), and a visit belonging to someone else produces a message that
 * says nothing about whether that visit exists.
 */

const api = vi.hoisted(() => {
  class ApiConflictError extends Error {}

  return {
    ApiConflictError,
    createDraft: vi.fn(),
    forAppointment: vi.fn(),
    /*
     * Next's redirect() throws, and that is load-bearing.
     *
     * Modelling it as a throw is what makes these tests able to catch a redirect placed
     * inside a try/catch — which would swallow the control-flow signal and return an
     * error state to a clinician whose note was created perfectly well.
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
vi.mock("@/lib/api/notes", () => ({
  ApiConflictError: api.ApiConflictError,
  notesApi: { createDraft: api.createDraft, forAppointment: api.forAppointment },
}));

import { startNote } from "./actions";
import { INITIAL_START_NOTE_STATE } from "./state";

const VISIT = "8f3c1d2e-0000-4000-8000-000000000001";
const NEW_NOTE = "8f3c1d2e-0000-4000-8000-000000000002";
const EXISTING_NOTE = "8f3c1d2e-0000-4000-8000-000000000003";

function run(visitPublicId?: string) {
  const data = new FormData();
  if (visitPublicId !== undefined) data.append("visitPublicId", visitPublicId);
  return startNote(INITIAL_START_NOTE_STATE, data);
}

describe("startNote", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("creates a draft for a visit with no note and opens it", async () => {
    api.createDraft.mockResolvedValue({ publicId: NEW_NOTE });

    await expect(run(VISIT)).rejects.toThrow(/NEXT_REDIRECT/);

    expect(api.createDraft).toHaveBeenCalledWith({
      appointmentPublicId: VISIT,
      subjective: "",
      objective: "",
      assessment: "",
      plan: "",
    });
    expect(api.redirect).toHaveBeenCalledWith(`/notes/${NEW_NOTE}`);

    // The day view must show the note on the way back, not an unchanged "Start note".
    expect(api.revalidatePath).toHaveBeenCalledWith("/today");
  });

  /**
   * The case a UI-only guard misses.
   *
   * The day view knows whether a visit has a note as of the moment it rendered. A second
   * tab, a second tap, or a page held open over lunch can all put a note there in between
   * — and the API's 409 is what stops a duplicate clinical record being written.
   */
  it("opens the existing note instead of creating a second one", async () => {
    api.createDraft.mockRejectedValue(new api.ApiConflictError("This visit already has a note."));
    api.forAppointment.mockResolvedValue({ publicId: EXISTING_NOTE });

    await expect(run(VISIT)).rejects.toThrow(/NEXT_REDIRECT/);

    expect(api.createDraft).toHaveBeenCalledTimes(1);
    expect(api.redirect).toHaveBeenCalledWith(`/notes/${EXISTING_NOTE}`);
  });

  /**
   * A visit belonging to another provider and a visit that does not exist BOTH arrive
   * here as null, because the API answers 404 to each (D052). The action must not invent
   * a distinction the API deliberately refused to make.
   */
  it("says nothing about whether an unreachable visit exists", async () => {
    api.createDraft.mockResolvedValue(null);

    const result = await run(VISIT);

    expect(result.status).toBe("error");
    expect(api.redirect).not.toHaveBeenCalled();
    expect(result.message).not.toMatch(/another|provider|permission|not allowed|belongs/i);
  });

  it("fails safely when a conflict has no note behind it", async () => {
    api.createDraft.mockRejectedValue(new api.ApiConflictError("Already has a note."));
    api.forAppointment.mockResolvedValue(null);

    const result = await run(VISIT);

    expect(result.status).toBe("error");
    expect(api.redirect).not.toHaveBeenCalled();
  });

  /**
   * A visit the API refuses to document — cancelled, a no-show, or one that has not
   * started — arrives as a 409 with a sentence written for a clinician. The day view
   * normally renders that reason instead of the button, so reaching this path means the
   * schedule was stale; the reason is what makes the refusal actionable, and
   * VISIT_UNAVAILABLE would replace it with "refresh and try again", which is exactly
   * what will not help.
   *
   * Safe to surface: the gate only ever runs on a visit this provider can already see.
   * Anything belonging to someone else is a 404 long before it (D052).
   */
  it("explains a refusal the API described", async () => {
    api.createDraft.mockRejectedValue(
      new api.ApiConflictError("This visit was cancelled. There is nothing to document."),
    );
    api.forAppointment.mockResolvedValue(null);

    const result = await run(VISIT);

    expect(result.status).toBe("error");
    expect(result.message).toBe("This visit was cancelled. There is nothing to document.");
  });

  it("does not call the API without a visit", async () => {
    const result = await run();

    expect(result.status).toBe("error");
    expect(api.createDraft).not.toHaveBeenCalled();
    expect(api.forAppointment).not.toHaveBeenCalled();
  });

  /** An upstream failure must reach the clinician as a sentence, not as a stack trace. */
  it("does not surface a raw transport error", async () => {
    api.createDraft.mockRejectedValue(new Error("Notes API /notes failed with 503"));

    const result = await run(VISIT);

    expect(result.status).toBe("error");
    expect(result.message).not.toMatch(/503|Notes API/);
    expect(result.message).toBeTruthy();
  });
});
