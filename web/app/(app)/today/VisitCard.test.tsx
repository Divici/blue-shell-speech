import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";

/**
 * The note entry point on a visit card.
 *
 * Before this existed, /notes/[publicId] was reachable only by typing a URL — the note
 * feature was unreachable in the running product. These assert the affordance is there,
 * points at the right note, and never offers to start a second one.
 */

vi.mock("server-only", () => ({}));
vi.mock("./actions", () => ({ startNote: vi.fn() }));

import { VisitCard } from "./VisitCard";
import type { DayVisit } from "@/lib/api/schedule";

const BASE: DayVisit = {
  publicId: "8f3c1d2e-0000-4000-8000-000000000001",
  patientPublicId: "8f3c1d2e-0000-4000-8000-0000000000a1",
  patientFirstName: "Maya",
  patientLastName: "Reyes",
  appointmentType: "Therapy",
  // 14:00 Eastern. Synthetic, like every fixture in this repo.
  startUtc: "2026-06-15T18:00:00Z",
  durationMinutes: 60,
  status: "Scheduled",
  travelBlockMinutes: null,
  mileage: null,
  notes: null,
  notePublicId: null,
  noteStatus: null,
};

function visit(overrides: Partial<DayVisit> = {}): DayVisit {
  return { ...BASE, ...overrides };
}

describe("VisitCard note action", () => {
  it("offers to start a note when the visit has none", () => {
    render(<VisitCard visit={visit()} />);

    expect(screen.getByRole("button", { name: /start note/i })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /open note/i })).not.toBeInTheDocument();
  });

  /** The visit id has to reach the action, or it starts a note for nothing. */
  it("submits the visit it belongs to", () => {
    const { container } = render(<VisitCard visit={visit()} />);

    const field = container.querySelector('input[name="visitPublicId"]');
    expect(field).toHaveValue(BASE.publicId);
  });

  it("links to the existing note rather than offering to start another", () => {
    const notePublicId = "8f3c1d2e-0000-4000-8000-0000000000b2";

    render(<VisitCard visit={visit({ notePublicId, noteStatus: "Draft" })} />);

    expect(screen.getByRole("link", { name: /open note/i })).toHaveAttribute(
      "href",
      `/notes/${notePublicId}`,
    );
    expect(screen.queryByRole("button", { name: /start note/i })).not.toBeInTheDocument();
  });

  /**
   * "Which of today's notes still need signing" is the question at the end of a day, and
   * it is unanswerable if a draft and a signed note look identical on the schedule.
   */
  it("distinguishes a draft from a signed note", () => {
    const notePublicId = "8f3c1d2e-0000-4000-8000-0000000000b2";

    const draft = render(<VisitCard visit={visit({ notePublicId, noteStatus: "Draft" })} />);
    expect(screen.getByText(/draft/i)).toBeInTheDocument();
    draft.unmount();

    render(<VisitCard visit={visit({ notePublicId, noteStatus: "Signed" })} />);
    expect(screen.getByText(/signed/i)).toBeInTheDocument();
    expect(screen.queryByText(/draft/i)).not.toBeInTheDocument();
  });

  /**
   * Every card carries the same words, so the visible label alone leaves a screen-reader
   * user with a list of identical links. The patient's name disambiguates them.
   */
  it("names the patient in the accessible label", () => {
    render(<VisitCard visit={visit()} />);
    expect(screen.getByRole("button", { name: "Start note for Maya Reyes" })).toBeVisible();

    const documented = "8f3c1d2e-0000-4000-8000-0000000000b2";
    render(<VisitCard visit={visit({ notePublicId: documented, noteStatus: "Draft" })} />);
    expect(screen.getByRole("link", { name: "Open note for Maya Reyes" })).toBeVisible();
  });

  /**
   * WCAG 2.5.3, Label in Name: a speech-input user says what they can see. An accessible
   * name that does not start with the visible text leaves "click start note" doing nothing.
   */
  it("keeps the visible text inside the accessible name", () => {
    render(<VisitCard visit={visit()} />);

    const button = screen.getByRole("button");
    expect(button.getAttribute("aria-label")).toMatch(/^Start note/);
    expect(button).toHaveTextContent("Start note");
  });
});

/**
 * Visits that cannot be documented.
 *
 * The schedule is read on a phone between houses and every card looked identical, so one
 * mis-tap created an empty draft on a cancelled visit, a no-show, or next week's session.
 * The API refuses those (Appointment.DocumentationBlockedReason); the card explains it
 * before the tap rather than after.
 */
describe("VisitCard on a visit that cannot be documented", () => {
  // 14:00 Eastern on the day of the fixture visit, i.e. the visit has started.
  const DURING = new Date("2026-06-15T18:30:00Z");

  it("offers no note on a cancelled visit, and says why", () => {
    render(<VisitCard visit={visit({ status: "Cancelled" })} now={DURING} />);

    expect(screen.queryByRole("button", { name: /start note/i })).not.toBeInTheDocument();
    // The sentence, not the status badge — which also reads "Cancelled".
    expect(screen.getByText(/nothing to document/i)).toHaveTextContent(/cancelled/i);
  });

  it("offers no note on a no-show, and says why", () => {
    render(<VisitCard visit={visit({ status: "NoShow" })} now={DURING} />);

    expect(screen.queryByRole("button", { name: /start note/i })).not.toBeInTheDocument();
    expect(screen.getByText(/no-show/i)).toBeInTheDocument();
  });

  it("offers no note on a visit that has not started", () => {
    render(
      <VisitCard visit={visit()} now={new Date("2026-06-15T17:00:00Z")} />,
    );

    expect(screen.queryByRole("button", { name: /start note/i })).not.toBeInTheDocument();
    expect(screen.getByText(/not started/i)).toBeInTheDocument();
  });

  /**
   * A note written before the visit was called off is still a clinical record. The gate
   * removes the entry point, never the way back to a note that already exists.
   */
  it("still links to a note that already exists on a cancelled visit", () => {
    const notePublicId = "8f3c1d2e-0000-4000-8000-0000000000b2";

    render(
      <VisitCard
        visit={visit({ status: "Cancelled", notePublicId, noteStatus: "Signed" })}
        now={DURING}
      />,
    );

    expect(screen.getByRole("link", { name: /open note/i })).toHaveAttribute(
      "href",
      `/notes/${notePublicId}`,
    );
  });
});
