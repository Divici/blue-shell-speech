import { describe, it, expect } from "vitest";
import { documentationBlockedReason } from "./visit-documentation";
import type { DaySchedule } from "@/lib/api/schedule";

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

/*
 * RECORDED RESPONSES, not hand-written objects.
 *
 * Both strings below are verbatim bodies from `GET /appointments/day/2026-06-15`, captured
 * from the API running against real SQL Server in Testcontainers on 2026-08-25. Identifiers
 * are the synthetic ones the integration fixtures create; there is no real patient here and
 * never will be (CLAUDE.md #1).
 *
 * They exist because every fixture in this file used to be written by hand, and every one
 * of them spelled `startUtc` with a Z that the endpoint was not sending. A suite whose
 * inputs are invented by the same person who wrote the code can only test what that person
 * already believed. These are the bytes that actually went over the wire.
 *
 * SERVED is what the endpoint sends now. SERVED_BEFORE_THE_FIX is the same response with
 * the UTC value converter removed from PracticeDbContext — kept, not deleted, because the
 * claim being made is that the two describe the same instant and the app must agree. Delete
 * the converter and the API suite goes red; keep this and the web suite proves the reading
 * does not depend on it.
 */

const SERVED =
  '{"date":"2026-06-15","visits":[{"publicId":"476fe488-f2ce-4e95-aeda-a3db89638380",' +
  '"patientPublicId":"f8a57815-010a-4db3-b882-05b08a31696d","patientFirstName":"Maya",' +
  '"patientLastName":"Reyes","appointmentType":"Therapy","startUtc":"2026-06-15T13:00:00Z",' +
  '"durationMinutes":45,"status":"Scheduled","travelBlockMinutes":30,"mileage":null,' +
  '"notes":"Bring the picture cards.","notePublicId":"3c9c08a4-35b3-401c-bb40-e12d72cfd836",' +
  '"noteStatus":"Draft"},{"publicId":"b61c2792-fc55-45c9-b1f1-e86f9aa2d31b",' +
  '"patientPublicId":"f8a57815-010a-4db3-b882-05b08a31696d","patientFirstName":"Maya",' +
  '"patientLastName":"Reyes","appointmentType":"Therapy","startUtc":"2026-06-15T16:00:00Z",' +
  '"durationMinutes":45,"status":"Cancelled","travelBlockMinutes":null,"mileage":null,' +
  '"notes":null,"notePublicId":null,"noteStatus":null}],"totalMileage":0}';

const SERVED_BEFORE_THE_FIX = SERVED.replaceAll(/(\d\d:\d\d:\d\d)Z/g, "$1");

/** 09:00 America/New_York on the day of the recorded schedule — the visit's own start. */
const NINE_AM_ET = new Date("2026-06-15T13:00:00Z");

/** One visit out of a recorded body, failing loudly if the payload has changed shape. */
function recordedVisit(payload: string, index: number) {
  const visit = (JSON.parse(payload) as DaySchedule).visits[index];
  if (!visit) throw new Error(`The recorded payload has no visit at index ${index}.`);
  return visit;
}

describe("documentationBlockedReason, against a recorded day payload", () => {
  /**
   * The live defect, stated as the reviewer found it.
   *
   * 09:00 ET is 13:00 UTC. Read as local time in Maryland that is 17:00 UTC — four hours
   * in the future — so the card said "This visit has not started yet" for the whole
   * morning and offered no way to document a session that was happening in the room.
   *
   * Control: parseApiInstant, via visit-documentation.ts.
   * Deleted (back to `new Date(visit.startUtc)`) → red on the SERVED_BEFORE_THE_FIX case,
   * "expected 'This visit has not started yet…' to be null".
   */
  it("offers the note at the visit's own start time, however the instant is spelled", () => {
    for (const [label, payload] of [
      ["as served", SERVED],
      ["as served before the fix", SERVED_BEFORE_THE_FIX],
    ] as const) {
      expect(
        documentationBlockedReason(recordedVisit(payload, 0), NINE_AM_ET),
        label,
      ).toBeNull();
    }
  });

  /**
   * Control: documentationBlockedReason — the `visit.status === "Cancelled"` branch.
   * Deleted → red on the toMatch, "AssertionError: expected 'This visit has not started
   * yet. Its n…' to match /cancelled/i" — the clock branch answers instead, and a
   * cancelled visit is offered its note the moment its start time passes.
   */
  it("still refuses a cancelled visit from the same payload", () => {
    expect(documentationBlockedReason(recordedVisit(SERVED, 1), NINE_AM_ET)).toMatch(
      /cancelled/i,
    );
  });

  /**
   * One minute before the recorded start, the note has genuinely not opened yet.
   *
   * Control: documentationBlockedReason — the `? NOT_STARTED` arm of the final comparison.
   * Deleted (replaced with `null`) → red on the toMatch, "TypeError: .toMatch() expects to
   * receive a string, but got object" — null, meaning the card would offer to document a
   * visit that has not begun.
   */
  it("holds the note back until the visit begins", () => {
    const oneMinuteEarly = new Date(NINE_AM_ET.getTime() - 60_000);

    expect(
      documentationBlockedReason(recordedVisit(SERVED_BEFORE_THE_FIX, 0), oneMinuteEarly),
    ).toMatch(/not started/i);
  });
});
