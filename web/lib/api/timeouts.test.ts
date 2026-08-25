import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
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
   * module and compares it with `DatabaseTimeouts.Ceiling`, which is the request bound plus
   * the uncancellable tail rather than the request bound alone. That comparison used to be
   * against the request bound, which is how this constant came to sit four minutes under
   * the API's real worst case. Repeating it here would restate one of the two constants,
   * which is the shape of the defect this whole task is about. What is worth pinning on
   * this side is that the value is a real duration and not, say, seconds mistaken for
   * milliseconds.
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
 *
 * THIS LIST IS NOT THE GUARD, and used to be treated as one. These are behavioural: they
 * call the client and inspect what reached `fetch`, which is worth having and is the only
 * thing that can catch a signal that is constructed and then not passed. What they cannot
 * do is notice a module nobody added — `lib/api/enquiries.ts` arrived and no list here
 * grew. The walk at the bottom of this file is what covers the set; the block below covers
 * the behaviour. (`enquiries.ts` asserts its own bound in `enquiries.test.ts`, next to the
 * rest of its contract.)
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
 * THE CALL SITE THAT DOES NOT EXIST YET.
 *
 * The tests above drive the seven fetches there are today. An eighth added tomorrow would
 * leave every one of them green, so this block does not name any: it WALKS THE TREE, finds
 * every module that calls `fetch`, and requires each call to carry both bounds. A new
 * client arrives covered or arrives red.
 *
 * IT USED TO BE A HARD-CODED LIST OF FIVE PATHS while its own comment claimed a new call
 * site "arrives bounded or arrives red". It did not: `lib/api/enquiries.ts` shipped in the
 * consultation-inbox work, was never added to the list, and was checked by nothing on
 * either side of the boundary — the API's cross-tree test reads only the constant and
 * never looks at a call site. It happened to be written correctly, which is the worst way
 * to find out a guard is not guarding. Same argument as the API's wire-format test
 * matching every property ending in `Utc` rather than a list somebody remembered to extend
 * (D072).
 *
 * BOTH BOUNDS, because the same hole had two things falling through it. `cache: "no-store"`
 * is ranked #1 in docs/THREAT_MODEL.md and had NO cross-file guard at all — only per-file
 * assertions in two of the seven modules' own test files, which is the same defect one
 * layer down.
 *
 * Control: any `signal: apiSignal()` in a module this walk finds.
 * Deleted from notes.ts → red, "AssertionError: web/lib/api/notes.ts calls fetch 1 time(s),
 * passes apiSignal() 0 time(s) and sets cache: no-store 1 time(s). An unbounded call to
 * the API is a request this tier will wait on forever; a cacheable one is patient data in
 * a shared cache."
 *
 * Control: any `cache: "no-store"` in a module this walk finds.
 * Deleted from patients.ts → red, same sentence with "calls fetch 1 time(s), passes
 * apiSignal() 1 time(s) and sets cache: no-store 0 time(s)".
 *
 * Control: the walk reaching outside this directory.
 * `SEARCH_ROOTS` reduced to `["lib/api"]` → red on "walks the tree rather than a list",
 * "AssertionError: Only 7 source file(s) found under lib/api. The walk is not reaching
 * this app's code.: expected 7 to be greater than 20" — and `lib/auth/api-client.ts`
 * silently drops out of the run, which is why that floor assertion exists at all and why
 * the discovered list is printed on every failure below.
 */
describe("the count of fetches and the count of bounds", () => {
  /*
   * Every tree this app writes network code in. `app` is here as well as `lib` because a
   * server action or a route handler can call the API directly, and one that did would sit
   * outside a walk that only knew about the client modules.
   */
  const SEARCH_ROOTS = ["lib", "app"];

  const WEB_ROOT = path.resolve(__dirname, "..", "..");

  /** Build output and dependencies, which are neither this repo's code nor small. */
  const SKIP_DIRECTORIES = new Set(["node_modules", ".next"]);

  function sourceFilesUnder(directory: string): string[] {
    const found: string[] = [];

    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);

      if (entry.isDirectory()) {
        if (!SKIP_DIRECTORIES.has(entry.name)) found.push(...sourceFilesUnder(full));
        continue;
      }

      // Test files are excluded: they mock `fetch` rather than calling the API, so a
      // `fetch(` in one is a stand-in and not a call site.
      if (/\.(ts|tsx)$/.test(entry.name) && !/\.test\.(ts|tsx)$/.test(entry.name)) {
        found.push(full);
      }
    }

    return found;
  }

  const scanned = SEARCH_ROOTS.flatMap((root) =>
    sourceFilesUnder(path.join(WEB_ROOT, root)),
  );

  const clients = scanned
    .map((file) => ({
      relative: path.relative(WEB_ROOT, file).split(path.sep).join("/"),
      source: readFileSync(file, "utf8"),
    }))
    .filter(({ source }) => /\bfetch\(/.test(source));

  /**
   * The walk found something, and found a lot of it.
   *
   * An `it.each` over an empty array registers no tests and the file stays green, so a
   * rename that broke the walk would delete this guard silently — which is the exact
   * failure mode being fixed. Both floors are deliberately loose: naming a count would put
   * a list back, one layer along.
   */
  it("walks the tree rather than a list", () => {
    expect(
      scanned.length,
      `Only ${scanned.length} source file(s) found under ${SEARCH_ROOTS.join(", ")}. ` +
        `The walk is not reaching this app's code.`,
    ).toBeGreaterThan(20);

    expect(
      clients.length,
      `No module calling fetch was found under ${SEARCH_ROOTS.join(", ")}. Either this ` +
        `tier stopped talking to the API or the walk stopped working.`,
    ).toBeGreaterThan(0);
  });

  it.each(clients.map((client) => [client.relative, client.source] as const))(
    "%s bounds every call it makes",
    (relative, source) => {
      const fetches = source.match(/\bfetch\(/g)?.length ?? 0;
      const signals = source.match(/signal:\s*apiSignal\(\)/g)?.length ?? 0;
      const noStore = source.match(/cache:\s*"no-store"/g)?.length ?? 0;

      const complaint =
        `web/${relative} calls fetch ${fetches} time(s), passes apiSignal() ` +
        `${signals} time(s) and sets cache: no-store ${noStore} time(s). An unbounded ` +
        `call to the API is a request this tier will wait on forever; a cacheable one ` +
        `is patient data in a shared cache. Modules found by the walk: ` +
        `${clients.map((c) => c.relative).join(", ")}.`;

      expect(signals, complaint).toBe(fetches);
      expect(noStore, complaint).toBe(fetches);
    },
  );
});
