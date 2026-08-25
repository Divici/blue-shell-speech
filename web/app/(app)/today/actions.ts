"use server";

/**
 * AT A GLANCE
 * -----------
 * Server actions for the day view.
 *
 * The `"use server"` directive above changes what these functions ARE. They look like
 * ordinary functions and a form calls them like ordinary functions — but they run on the
 * server, never in the browser. Next.js creates the HTTP endpoint, sends the request, and
 * returns the result, all of which would otherwise be a hand-written `fetch`.
 *
 * That matters here because it means the browser never talks to the .NET API. It talks to
 * this file, this file talks to the API, and the session identity is read server-side —
 * so nothing the browser submits can name a different clinician.
 */

import { redirect } from "next/navigation";
import { revalidatePath } from "next/cache";
import { notesApi, ApiConflictError } from "@/lib/api/notes";
import { VISIT_UNAVAILABLE, type StartNoteState } from "./state";

/**
 * Opens the clinical note for a visit, creating the draft if there is not one yet.
 *
 * A server action rather than a client fetch: creating a clinical record is a mutation,
 * and the browser never talks to the .NET API (D003, CLAUDE.md non-negotiable #6). The
 * provider identity comes from the encrypted session cookie inside notesApi, so nothing
 * the browser sends can name a different clinician.
 *
 * NOTHING HERE IS LOGGED — the same rule as every other note action.
 */
export async function startNote(
  _previous: StartNoteState,
  formData: FormData,
): Promise<StartNoteState> {
  const visitPublicId = String(formData.get("visitPublicId") ?? "").trim();

  if (!visitPublicId) {
    return { status: "error", message: VISIT_UNAVAILABLE };
  }

  let notePublicId: string | null = null;

  /*
   * The API's own wording, kept for the case where no note turns out to be behind the
   * conflict — a cancelled visit, a no-show, or one that has not started yet. Those are
   * rules the clinician needs to read; VISIT_UNAVAILABLE would replace them with "refresh
   * and try again", which is the one thing that will not help.
   *
   * Safe to surface: the API only reaches that check on a visit this provider can already
   * see. Anything belonging to somebody else is a 404 well before it (D052).
   */
  let refusal: string | null = null;

  try {
    /*
     * Create first, ask second.
     *
     * The day view already knows which visits have notes, so this path only runs when it
     * believed there was none. Checking again before creating would add a round trip to
     * every start AND still lose the race — the API's one-current-note-per-visit rule
     * (409, plus a filtered unique index behind it) is the thing that actually holds.
     */
    const created = await notesApi.createDraft({
      appointmentPublicId: visitPublicId,
      subjective: "",
      objective: "",
      assessment: "",
      plan: "",
    });

    notePublicId = created?.publicId ?? null;
  } catch (error) {
    if (!(error instanceof ApiConflictError)) {
      return {
        status: "error",
        message: "We could not open a note for this visit. Please try again.",
      };
    }

    refusal = error.message;

    /*
     * The visit gained a note between the page rendering and this tap — another tab, a
     * double tap, or a schedule left open. Opening the one that exists is what the
     * clinician wanted; a duplicate clinical record is not.
     */
    try {
      const existing = await notesApi.forAppointment(visitPublicId);
      notePublicId = existing?.publicId ?? null;
    } catch {
      notePublicId = null;
    }
  }

  if (!notePublicId) {
    return { status: "error", message: refusal ?? VISIT_UNAVAILABLE };
  }

  // So the schedule shows "Open note" rather than "Start note" on the way back.
  revalidatePath("/today");

  /*
   * OUTSIDE the try/catch above, deliberately.
   *
   * redirect() signals by throwing. Called inside a catch-all it would be caught as a
   * failure, and a clinician whose note was created perfectly well would be told it was
   * not.
   */
  redirect(`/notes/${notePublicId}`);
}
