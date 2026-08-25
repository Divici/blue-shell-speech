import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { readFileSync } from "node:fs";
import path from "node:path";

vi.mock("server-only", () => ({}));
vi.mock("@/lib/auth/session", () => ({
  getSession: async () => ({
    providerPublicId: "0f3c1c2e-6b7a-4a1e-9c3d-2f5a8b1c4d6e",
    displayName: "Michelle",
  }),
}));

import { API_TIMEOUT_MS, apiSignal } from "./timeouts";
import { notesApi, goalsApi } from "./notes";
import { patientsApi } from "./patients";
import { scheduleApi } from "./schedule";
import { consultationsApi } from "./consultations";
import { authApi } from "@/lib/auth/api-client";

/**
 * EVERY CALL THIS TIER MAKES TO THE API IS BOUNDED.
 *
 * Five of the six were not. `AbortSignal.timeout` appeared exactly once in the whole web
 * tree — on the public consultation form — while `notes.ts`, `patients.ts`, `schedule.ts`
 * (twice) and `auth/api-client.ts` passed no signal at all, so a clinician's request
 * against a wedged API had no bound on this side of the boundary whatsoever.
 *
 * The API's own comment meanwhile justified a thirty-second request timeout with "the BFF
 * gives up at twenty-five (web/lib/api)". It did not, and nothing could tell: a claim about
 * one tree, written in a comment in another. The relationship between the two numbers is
 * asserted from the API side, which reads this file; what is asserted here is that the
 * clients actually use it.
 *
 * SYNTHETIC DATA ONLY.
 */

/** A single OK response, and a record of exactly how fetch was called. */
function capturingFetch(body: unknown = { publicId: "x" }): typeof fetch {
  return vi.fn(
    async () =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
  ) as unknown as typeof fetch;
}

function initOf(mock: typeof fetch): RequestInit {
  const calls = (mock as unknown as ReturnType<typeof vi.fn>).mock.calls;
  expect(calls).toHaveLength(1);
  return (calls[0] as [string, RequestInit])[1];
}

let originalFetch: typeof fetch;

beforeEach(() => {
  originalFetch = globalThis.fetch;
  process.env.API_BASE_URL = "http://api.test";
  vi.spyOn(console, "error").mockImplementation(() => {});
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  vi.restoreAllMocks();
});

describe("apiSignal", () => {
  it("hands out a signal that has not already expired", () => {
    const signal = apiSignal();

    expect(signal).toBeInstanceOf(AbortSignal);
    expect(signal.aborted).toBe(false);
  });

  /**
   * The number itself is asserted from the API side —
   * `RequestBoundsTests.The_bff_waits_longer_than_this_api_is_prepared_to_spend` reads this
   * module and compares it with `DatabaseTimeouts.Request`. Repeating that comparison here
   * would restate one of the two constants, which is the shape of the defect this whole
   * task is about. What is worth pinning on this side is that the value is a real duration
   * and not, say, seconds mistaken for milliseconds.
   */
  it("is measured in milliseconds and is minutes rather than seconds", () => {
    expect(API_TIMEOUT_MS).toBeGreaterThan(60_000);
  });
});

/*
 * ONE `it` PER CALL SITE, because the defect was per call site.
 *
 * A single test through one client would have been just as green while the other five went
 * unbounded — which is precisely what happened. Each of these names the client whose fetch
 * it drives.
 */
describe("every BFF fetch carries the API timeout", () => {
  /**
   * Control: the `signal: apiSignal()` on the fetch in notes.ts `request`.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("notes.ts — the clinical note client", async () => {
    const fetchMock = capturingFetch({ publicId: "n" });
    globalThis.fetch = fetchMock;

    await notesApi.forAppointment("a5b2c1d0-1111-2222-3333-444455556666");

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });

  /**
   * The goals client shares `request` with the notes client, so this asserts the same
   * line. Kept because the two are separate exports and a future split would leave one of
   * them uncovered without it.
   *
   * Control: the `signal: apiSignal()` on the fetch in notes.ts `request`.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("notes.ts — the goals client", async () => {
    const fetchMock = capturingFetch([]);
    globalThis.fetch = fetchMock;

    await goalsApi.list("a5b2c1d0-1111-2222-3333-444455556666");

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });

  /**
   * Control: the `signal: apiSignal()` on the fetch in patients.ts `request`.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("patients.ts", async () => {
    const fetchMock = capturingFetch([]);
    globalThis.fetch = fetchMock;

    await patientsApi.list();

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });

  /**
   * Control: the `signal: apiSignal()` on the fetch in schedule.ts `request`.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("schedule.ts — the day query", async () => {
    const fetchMock = capturingFetch({ date: "2026-06-15", visits: [], totalMileage: 0 });
    globalThis.fetch = fetchMock;

    await scheduleApi.day("2026-06-15");

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });

  /**
   * `scheduleApi.create` does NOT go through that module's `request` helper — it has a
   * fetch of its own, because it needs to read a 409 body. Two fetches in one file is
   * exactly how the sibling gets missed.
   *
   * Control: the `signal: apiSignal()` on the fetch inside scheduleApi.create.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("schedule.ts — booking a visit", async () => {
    const fetchMock = capturingFetch({ publicId: "v" });
    globalThis.fetch = fetchMock;

    await scheduleApi.create({
      patientPublicId: "a5b2c1d0-1111-2222-3333-444455556666",
      appointmentType: "Therapy",
      startUtc: "2026-06-15T13:00:00Z",
      durationMinutes: 45,
      travelBlockMinutes: null,
      notes: null,
    });

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });

  /**
   * Control: the `signal: apiSignal()` on the fetch in auth/api-client.ts `post`.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("auth/api-client.ts — the sign-in path", async () => {
    const fetchMock = capturingFetch({ status: "invalid", userId: null });
    globalThis.fetch = fetchMock;

    await authApi.verifyPassword("michelle@example.com", "correct-horse-battery-staple");

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });

  /**
   * The one that always had a bound — at 25 seconds, which sat UNDER the API's own retry
   * budget and therefore reported "not stored" to a parent whose enquiry the API went on
   * to commit. The comment above it says there is deliberately no retry because a POST
   * that timed out may well have succeeded; that reasoning argues for waiting, not for
   * giving up first.
   *
   * Control: the `signal: apiSignal()` on the fetch in consultations.ts `submit`.
   * Deleted → red, "AssertionError: expected undefined to be an instance of AbortSignal".
   */
  it("consultations.ts — the public intake form", async () => {
    const fetchMock = capturingFetch({ publicId: "c" });
    globalThis.fetch = fetchMock;

    await consultationsApi.submit({
      parentName: "Jordan Reyes",
      email: "jordan@example.com",
      phone: "410-555-0142",
      childFirstName: "Maya",
      childAgeMonths: 30,
      concerns: "She has about ten words but is not combining them.",
      preferredContact: "Either",
      sourceIpHash:
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
    });

    expect(initOf(fetchMock).signal).toBeInstanceOf(AbortSignal);
  });
});

/**
 * THE SEVENTH CALL SITE, which does not exist yet.
 *
 * The tests above drive the six clients there are today, and a seventh added tomorrow
 * would leave them all green. This one counts instead: every `fetch(` in a module that
 * talks to the API must be accompanied by an `apiSignal()`, so a new call arrives bounded
 * or arrives red. Same argument as the API's wire-format test matching every property
 * ending in `Utc` rather than a list somebody remembered to extend (D072).
 *
 * Control: any `signal: apiSignal()` in the files listed below.
 * Deleted from notes.ts → red, "AssertionError: web/lib/api/notes.ts makes 1 call(s) to
 * fetch and passes apiSignal() 0 time(s)".
 */
describe("the count of fetches and the count of timeouts", () => {
  const CLIENTS = [
    "lib/api/notes.ts",
    "lib/api/patients.ts",
    "lib/api/schedule.ts",
    "lib/api/consultations.ts",
    "lib/auth/api-client.ts",
  ];

  it.each(CLIENTS)("%s bounds every call it makes", (relative) => {
    const source = readFileSync(
      path.resolve(__dirname, "..", "..", relative),
      "utf8",
    );

    const fetches = source.match(/\bfetch\(/g)?.length ?? 0;
    const signals = source.match(/signal:\s*apiSignal\(\)/g)?.length ?? 0;

    expect(fetches).toBeGreaterThan(0);
    expect(
      signals,
      `web/${relative} makes ${fetches} call(s) to fetch and passes apiSignal() ` +
        `${signals} time(s). An unbounded call to the API is a request this tier will ` +
        `wait on forever.`,
    ).toBe(fetches);
  });
});
