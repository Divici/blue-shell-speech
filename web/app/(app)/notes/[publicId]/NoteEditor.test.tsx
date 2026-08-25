import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

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
import { saveDraft, signNote, amendNote, discardDraft } from "./actions";
import { INITIAL_NOTE_STATE } from "./state";
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
  isAmendment: false,
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
   * An amendment is a draft, and clearing it is an ordinary edit — so every emptiness
   * question this control asks is satisfied, and the screen used to offer "Discard this
   * empty note" on the one row that must never be discarded.
   *
   * That is worse than a control the API happens to refuse. The copy claims nothing has
   * been saved in the note, which is false: a signed version sits underneath it, already
   * marked Amended with IsCurrent = 0. Offering the tap leads a clinician to a refusal she
   * had no reason to expect, on a screen that told her the opposite.
   *
   * Control: NoteEditor.isEmptyNote — the `if (note.isAmendment) return false;` line.
   * Deleted → red on `expect(discardControl()).not.toBeInTheDocument()`:
   * "expected document not to contain element, found <button … >Discard this empty
   * note</button> instead".
   *
   * The line this used to name — `!note.isAmendment` — is not in the file and never was,
   * and the message it quoted is not the one the deletion produces. D070's whole argument
   * is that the sentence cannot be written without running the deletion; it was written
   * without running the deletion, in the commit that introduced D070.
   */
  it("is absent on a cleared amendment", () => {
    render(
      <NoteEditor
        note={note({
          versionNumber: 2,
          isAmendment: true,
          amendmentReason: "Corrected the accuracy figure.",
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

/**
 * What the editor does while the API is answering.
 *
 * SIGNING IS THE IRREVERSIBLE ONE. A signed note is immutable — corrections become new
 * versions and TR_ClinicalNotes_PreventSignedEdits enforces it in the database — so "tap
 * it again because nothing happened" is the worst available response to a slow answer,
 * and it was the only response this screen offered: the sign button was a bare button with
 * no pending state and no disabled attribute, sitting beside a save button that had both.
 *
 * The waits are not hypothetical. `api` scales to zero and its database auto-pauses, so a
 * first sign of the day pays a cold start (docs/PERFORMANCE.md, ~22 s) before the write
 * even begins.
 *
 * REACT ALREADY SERIALISES useActionState SUBMISSIONS, AND THAT IS NOT THE SAME AS
 * PREVENTING THEM. Written the obvious way — three clicks, then assert one call — these
 * tests were green against the code as committed, because the second and third
 * submissions were QUEUED rather than dropped and had not run at the moment of the
 * assertion. They fire the instant the first resolves. So each one below releases the held
 * action and waits for the control to come back before counting: the difference between
 * "deferred" and "did not happen" only exists after the first has finished. D066's class,
 * found by running the deletion rather than by reading the assertion.
 */
describe("NoteEditor while an action is in flight", () => {
  beforeEach(() => vi.clearAllMocks());

  /**
   * An action that hangs until released, so an in-flight state exists long enough to
   * assert on. Only the FIRST call is held; anything the UI lets through afterwards
   * resolves immediately and is still counted.
   */
  function holdable() {
    let release: () => void = () => {};
    let first = true;

    const impl = () => {
      if (!first) return Promise.resolve(INITIAL_NOTE_STATE);
      first = false;
      return new Promise<typeof INITIAL_NOTE_STATE>((resolve) => {
        release = () => resolve(INITIAL_NOTE_STATE);
      });
    };

    return { impl, release: () => release() };
  }

  /**
   * Releases the held action and waits for the control to come back.
   *
   * EVERY TEST HERE MUST END WITH THIS, and finding out why cost an hour. An action left
   * hanging when a test finishes is not confined to that test: the next one's release has
   * no effect, its button stays disabled, and it fails with "Unable to find role=button
   * and name /^sign note$/i" — while passing in isolation, which is the worst shape a
   * failure can have. React runs these submissions inside a transition, and an
   * unresolved one outlives the unmount that Testing Library's cleanup performs.
   *
   * It also does the counting work: React QUEUES a second submission rather than dropping
   * it, so "one call" is only meaningful once the first has resolved and the queue would
   * have drained.
   */
  async function settle(held: { release: () => void }, label = /^sign note$/i) {
    held.release();
    await waitFor(() => expect(screen.getByRole("button", { name: label })).toBeEnabled());
  }

  const draft = () => note({ subjective: "Mum reports steady progress." });

  const signed = () =>
    note({
      status: "Signed",
      subjective: "Mum reports steady progress.",
      signedAtUtc: "2026-06-15T20:00:00Z",
      signedBy: "Michelle",
    });

  /**
   * Control: the `disabled={busy}` attribute in NoteEditor.ActionButton — the one both
   * submit controls share.
   * Deleted → red on ALL SEVEN tests in this block. This one reports it directly:
   * "expect(element).toBeDisabled() … Received element is not disabled". The four
   * counting tests fail inside `settle` instead, because the queued second submission
   * starts the moment the first resolves and the control never comes back — which is the
   * defect stated as a timeout rather than as a count.
   */
  it("disables the sign control while the signature is being written", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signNote).mockImplementation(held.impl);

    render(<NoteEditor note={draft()} />);
    await user.click(screen.getByRole("button", { name: /^sign note$/i }));

    expect(screen.getByRole("button", { name: /signing/i })).toBeDisabled();
    await settle(held);
  });

  /**
   * The label is the part a screen reader gets. A control that greys out and says the same
   * thing has told a sighted clinician something and a blind one nothing.
   *
   * Control: the `{pending ? pendingLabel : label}` expression in ActionButton.
   * Reduced to `{label}` → red, "Unable to find an accessible element with the role
   * \"button\" and name `/signing/i`", along with six others: the discard and amendment
   * labels come from the same pattern, so the whole block goes with it.
   */
  it("says that it is signing rather than only greying out", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signNote).mockImplementation(held.impl);

    render(<NoteEditor note={draft()} />);
    await user.click(screen.getByRole("button", { name: /^sign note$/i }));

    expect(screen.getByRole("button", { name: /signing/i })).toBeInTheDocument();
    await settle(held);
  });

  /**
   * THE DOUBLE-SUBMIT, ASSERTED AS BEHAVIOUR rather than as an attribute.
   *
   * toBeDisabled proves the attribute is set; this proves the attribute does the job,
   * which is the actual requirement — a second POST to a note must not be reachable from
   * this UI. A clinician on a slow connection taps three times; the action is entered
   * once, and is still one after the first has finished and any queue would have drained.
   *
   * Control: the `disabled={busy}` attribute in ActionButton.
   * Deleted → red in `settle`: the second and third submissions run as soon as the first
   * resolves, so the button is still "Signing…" when the wait expires.
   */
  it("cannot be made to sign the same note twice", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signNote).mockImplementation(held.impl);

    render(<NoteEditor note={draft()} />);
    const sign = screen.getByRole("button", { name: /^sign note$/i });

    await user.click(sign);
    await user.click(sign);
    await user.click(sign);

    await settle(held);

    expect(vi.mocked(signNote)).toHaveBeenCalledTimes(1);
  });

  /**
   * Saving and signing share a form, and the second must not start while the first is in
   * flight. Save posts the textareas; sign attests to them. Signing on top of an
   * unfinished save would attest to a version the API has not stored.
   *
   * Control: the `disabled={busy}` attribute in ActionButton — `busy`, not `saving`,
   * which is what makes it cover the sibling action.
   * Deleted → red, "Unable to find an accessible element with the role \"button\" and
   * name `/^save draft$/i`" — the save button had entered its own pending state, which
   * is the second submission this exists to prevent.
   */
  it("will not start a save while a signature is in flight", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signNote).mockImplementation(held.impl);

    render(<NoteEditor note={draft()} />);
    await user.click(screen.getByRole("button", { name: /^sign note$/i }));
    await user.click(screen.getByRole("button", { name: /^save draft$/i }));

    await settle(held);

    expect(vi.mocked(saveDraft)).not.toHaveBeenCalled();
  });

  /**
   * Each control says what IT is doing.
   *
   * Both buttons submit one form, so a pending flag read from the FORM rather than from
   * the action relabels both — and the save button announced "Saving…" throughout a
   * signature. Telling a clinician the wrong operation is under way, on the screen where
   * the wrong operation is irreversible, is worse than telling her nothing.
   *
   * Control: the per-action `signing` / `saving` flags from useActionState.
   * Both replaced with the shared `busy` flag → red, "expected document not to contain
   * element, found <button …>Saving…</button>", plus three siblings.
   */
  it("does not claim to be saving while it is signing", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(signNote).mockImplementation(held.impl);

    render(<NoteEditor note={draft()} />);
    await user.click(screen.getByRole("button", { name: /^sign note$/i }));

    expect(screen.queryByRole("button", { name: /saving/i })).not.toBeInTheDocument();
    await settle(held);
  });

  /**
   * Discard is the other irreversible one, and it was the other bare button.
   *
   * The row goes for good — there is no undo, the visit reads as undocumented again, and
   * a second DELETE against an id that is already gone is a refusal the API audits (D087).
   * Nothing about that is harmful; all of it is noise generated by a UI that let a
   * clinician tap twice.
   *
   * Control: the `disabled={busy}` attribute on the discard button.
   * Deleted → red, "expect(element).toBeDisabled() … Received element is not disabled:
   * <button class=\"mt-3 inline-flex items-center gap-2 text-sm font-semibold
   * text-blue-deep …\" type=\"submit\" />".
   */
  it("cannot be made to discard the same draft twice", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(discardDraft).mockImplementation(held.impl);

    render(<NoteEditor note={note()} />);
    const discard = screen.getByRole("button", { name: /^discard this empty note$/i });

    await user.click(discard);
    expect(screen.getByRole("button", { name: /discarding/i })).toBeDisabled();
    await user.click(discard);

    await settle(held, /^discard this empty note$/i);

    expect(vi.mocked(discardDraft)).toHaveBeenCalledTimes(1);
  });

  /**
   * Amending a signed note is a write too — it creates version n+1 — and two of them is
   * two draft amendments against one signature, which
   * UX_ClinicalNotes_OneCurrentPerAppointment then refuses at the database.
   *
   * Control: the `disabled={busy}` attribute in ActionButton, which the amendment form
   * reaches with `busy={starting}`.
   * Deleted → red, "expect(element).toBeDisabled() … Received element is not disabled".
   */
  it("cannot be made to start two amendments", async () => {
    const user = userEvent.setup();
    const held = holdable();
    vi.mocked(amendNote).mockImplementation(held.impl);

    render(<NoteEditor note={signed()} />);
    await user.click(screen.getByRole("button", { name: /amend this note/i }));

    const start = screen.getByRole("button", { name: /^start amendment$/i });
    await user.click(start);
    expect(screen.getByRole("button", { name: /starting/i })).toBeDisabled();
    await user.click(start);

    await settle(held, /^start amendment$/i);

    expect(vi.mocked(amendNote)).toHaveBeenCalledTimes(1);
  });
});
