import { describe, it, expect } from "vitest";
import { documentationBlockedReason } from "./visit-documentation";

/**
 * Which visits offer a note, and which explain why they do not.
 *
 * The predicate exists so the schedule asks the same question the API asks
 * (Appointment.DocumentationBlockedReason) rather than a locally reinvented one — the
 * precedent D062 set for the AAC fields.
 */

const NOW = new Date("2026-06-15T18:00:00Z");

function visit(overrides: {
  status?: "Scheduled" | "Completed" | "Cancelled" | "NoShow";
  startUtc?: string;
}) {
  return {
    status: overrides.status ?? "Scheduled",
    startUtc: overrides.startUtc ?? "2026-06-15T17:00:00Z",
  } as const;
}

describe("documentationBlockedReason", () => {
  it("allows a visit that has already started", () => {
    expect(documentationBlockedReason(visit({}), NOW)).toBeNull();
  });

  it("allows a visit starting exactly now", () => {
    expect(
      documentationBlockedReason(visit({ startUtc: "2026-06-15T18:00:00Z" }), NOW),
    ).toBeNull();
  });

  it("blocks a cancelled visit", () => {
    const reason = documentationBlockedReason(visit({ status: "Cancelled" }), NOW);

    expect(reason).toMatch(/cancelled/i);
  });

  it("blocks a no-show", () => {
    const reason = documentationBlockedReason(visit({ status: "NoShow" }), NOW);

    expect(reason).toMatch(/no-show/i);
  });

  /** Even a cancellation of a visit that already started. It still did not happen. */
  it("blocks a cancelled visit whose start time has passed", () => {
    const reason = documentationBlockedReason(
      visit({ status: "Cancelled", startUtc: "2026-06-01T14:00:00Z" }),
      NOW,
    );

    expect(reason).toMatch(/cancelled/i);
  });

  it("blocks a visit that has not started", () => {
    const reason = documentationBlockedReason(
      visit({ startUtc: "2026-06-15T18:00:01Z" }),
      NOW,
    );

    expect(reason).toMatch(/not started/i);
  });

  /**
   * A visit marked complete happened, whatever the clock says — the same allowance the
   * aggregate makes, for a session closed a couple of minutes early.
   */
  it("allows a completed visit even before its start time", () => {
    expect(
      documentationBlockedReason(
        visit({ status: "Completed", startUtc: "2026-06-15T19:00:00Z" }),
        NOW,
      ),
    ).toBeNull();
  });
});
