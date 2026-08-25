import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";

/**
 * The discard affordance on an empty draft.
 *
 * A mis-tap on the schedule lands the clinician here, on the wrong child's note. The way
 * out has to be on this screen — it is the one she is looking at — and it has to disappear
 * the moment the note stops being empty, because from then on the row is a clinical record
 * that both the API and a database trigger refuse to delete.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({
  saveDraft: vi.fn(),
  signNote: vi.fn(),
  amendNote: vi.fn(),
  discardDraft: vi.fn(),
}));

import { NoteEditor } from "./NoteEditor";
import type { ClinicalNote } from "@/lib/api/notes";

const EMPTY_DRAFT: ClinicalNote = {
  publicId: "8f3c1d2e-0000-4000-8000-000000000002",
  versionNumber: 1,
  isCurrent: true,
  status: "Draft",
  subjective: "",
  objective: "",
  assessment: "",
  plan: "",
  origin: "Manual",
  signedAtUtc: null,
  signedBy: null,
  amendmentReason: null,
  integrityVerified: true,
};

function note(overrides: Partial<ClinicalNote> = {}): ClinicalNote {
  return { ...EMPTY_DRAFT, ...overrides };
}

const discardControl = () => screen.queryByRole("button", { name: /discard/i });

describe("NoteEditor discard control", () => {
  it("offers to discard an empty draft", () => {
    render(<NoteEditor note={note()} />);

    expect(discardControl()).toBeInTheDocument();
  });

  it("submits the note it belongs to", () => {
    const { container } = render(<NoteEditor note={note()} />);

    const fields = container.querySelectorAll('input[name="publicId"]');

    // One in the editor form, one in the discard form — a separate <form>, so the
    // discard cannot carry the editor's textareas along with it.
    expect(fields.length).toBe(2);
    fields.forEach((field) => expect(field).toHaveValue(EMPTY_DRAFT.publicId));
  });

  /**
   * One sentence in any section makes this a clinical record. Each is checked separately:
   * a control that only looked at Subjective would still offer to delete an objective
   * section somebody typed first.
   */
  it("disappears once any section has content", () => {
    const sections = ["subjective", "objective", "assessment", "plan"] as const;

    for (const section of sections) {
      const view = render(<NoteEditor note={note({ [section]: "Requesting 60%." })} />);

      expect(discardControl()).not.toBeInTheDocument();
      view.unmount();
    }
  });

  it("treats whitespace as empty, the way the API does", () => {
    render(<NoteEditor note={note({ subjective: "   " })} />);

    expect(discardControl()).toBeInTheDocument();
  });

  it("is absent on a signed note", () => {
    render(
      <NoteEditor
        note={note({
          status: "Signed",
          subjective: "Mum reports steady progress.",
          signedAtUtc: "2026-06-15T20:00:00Z",
          signedBy: "Michelle",
        })}
      />,
    );

    expect(discardControl()).not.toBeInTheDocument();
  });

  /**
   * D063's precedent: state the consequence next to the control rather than confirm it
   * afterwards. A dialog people click through is a decision that was never made.
   */
  it("states what discarding does before the tap", () => {
    render(<NoteEditor note={note()} />);

    expect(screen.getByText(/nothing has been saved/i)).toBeInTheDocument();
  });
});
