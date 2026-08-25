"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { notesApi, ApiConflictError } from "@/lib/api/notes";
import type { NoteActionState } from "./state";

/**
 * Note actions.
 *
 * NOTHING HERE IS LOGGED. Every field carries clinical observations about a child — the
 * most sensitive payload in the application (docs/SECURITY.md).
 */

export async function saveDraft(
  _previous: NoteActionState,
  formData: FormData,
): Promise<NoteActionState> {
  const publicId = String(formData.get("publicId") ?? "");

  const content = {
    subjective: String(formData.get("subjective") ?? ""),
    objective: String(formData.get("objective") ?? ""),
    assessment: String(formData.get("assessment") ?? ""),
    plan: String(formData.get("plan") ?? ""),
  };

  try {
    await notesApi.updateDraft(publicId, content);
  } catch (error) {
    if (error instanceof ApiConflictError) {
      // The note was signed — by another tab, or between load and save. The API's own
      // wording explains the rule.
      return { status: "error", message: error.message, values: content };
    }
    return {
      status: "error",
      message: "We could not save this note. Your text is still here — try again.",
      values: content,
    };
  }

  revalidatePath(`/notes/${publicId}`);
  return { status: "saved", values: content };
}

/**
 * Signs the note.
 *
 * Saves the current content FIRST, in the same action. Signing what is on screen — rather
 * than whatever was last persisted — is the only behaviour that matches what a clinician
 * believes they are attesting to.
 */
export async function signNote(
  _previous: NoteActionState,
  formData: FormData,
): Promise<NoteActionState> {
  const publicId = String(formData.get("publicId") ?? "");

  const content = {
    subjective: String(formData.get("subjective") ?? ""),
    objective: String(formData.get("objective") ?? ""),
    assessment: String(formData.get("assessment") ?? ""),
    plan: String(formData.get("plan") ?? ""),
  };

  try {
    await notesApi.updateDraft(publicId, content);
    await notesApi.sign(publicId);
  } catch (error) {
    if (error instanceof ApiConflictError) {
      return { status: "error", message: error.message, values: content };
    }
    return {
      status: "error",
      message: "We could not sign this note. Nothing has been changed.",
      values: content,
    };
  }

  revalidatePath(`/notes/${publicId}`);
  return { status: "signed", values: content };
}

/** Creates the next version. The current one is retained in full. */
export async function amendNote(
  _previous: NoteActionState,
  formData: FormData,
): Promise<NoteActionState> {
  const publicId = String(formData.get("publicId") ?? "");
  const reason = String(formData.get("reason") ?? "").trim();

  if (!reason) {
    return {
      status: "error",
      message: "Say why this note needs correcting — it becomes part of the record.",
    };
  }

  let amendment;
  try {
    amendment = await notesApi.amend(publicId, reason);
  } catch (error) {
    if (error instanceof ApiConflictError) {
      return { status: "error", message: error.message };
    }
    return { status: "error", message: "We could not start an amendment. Please try again." };
  }

  if (!amendment) {
    return { status: "error", message: "We could not start an amendment. Please try again." };
  }

  revalidatePath(`/notes/${publicId}`);
  redirect(`/notes/${amendment.publicId}`);
}
