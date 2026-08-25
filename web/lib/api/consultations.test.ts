import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

vi.mock("server-only", () => ({}));

import { consultationsApi, type ConsultationSubmission } from "./consultations";

/**
 * The one API call in this application that carries no session.
 *
 * Everything else in `lib/api` forwards the provider identity read from the encrypted
 * session cookie. This call is made on behalf of a parent who has no account, so the
 * questions here are different: what exactly goes on the wire, what comes back when the
 * API cannot take it, and whether the caller can tell "stored" from "not stored".
 *
 * SYNTHETIC DATA ONLY.
 */

const SUBMISSION: ConsultationSubmission = {
  parentName: "Jordan Reyes",
  email: "jordan@example.com",
  phone: "410-555-0142",
  childFirstName: "Maya",
  childAgeMonths: 30,
  concerns: "She has about ten words but is not combining them.",
  preferredContact: "Either",
  sourceIpHash:
    "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
};

const PUBLIC_ID = "3f7c9f2e-8a41-4c3d-9f0b-2d6c8e1a5b47";

function respondWith(status: number, body?: unknown): typeof fetch {
  return vi.fn(
    async () =>
      new Response(body === undefined ? null : JSON.stringify(body), {
        status,
        headers: { "content-type": "application/json" },
      }),
  ) as unknown as typeof fetch;
}

/** The first call’s arguments, typed loosely: this is a mock, not the DOM signature. */
function firstCall(
  mock: typeof fetch,
): [string, RequestInit & { cache?: string }] {
  const calls = (mock as unknown as ReturnType<typeof vi.fn>).mock.calls;
  expect(calls).toHaveLength(1);
  return calls[0] as [string, RequestInit & { cache?: string }];
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

describe("consultationsApi.submit", () => {
  /**
   * Control: the `publicId: body.publicId` in the success return.
   * Replaced with an id of our own making → red, "AssertionError: expected { stored: true,
   * …(1) } to deeply equal { stored: true, …(1) }". The id has to come from the API,
   * because it is the id of the row that exists.
   */
  it("posts the enquiry and reports the id it was stored under", async () => {
    globalThis.fetch = respondWith(201, { publicId: PUBLIC_ID });

    const result = await consultationsApi.submit(SUBMISSION);

    expect(result).toEqual({ stored: true, publicId: PUBLIC_ID });
  });

  /**
   * The field names are the API's request contract, and a rename on either side is a
   * silent data loss rather than a compile error — the two sides do not share types.
   *
   * `preferredContactMethod` is the one that differs from the form's own name for it, and
   * it is a STRING because the API matches it against the enum by name; an integer there
   * would be cast unchecked (see ConsultationEndpoints).
   *
   * Control: the request-body mapping in consultationsApi.submit.
   * The `preferredContactMethod` line deleted → red, "AssertionError: expected
   * { parentName: 'Jordan Reyes', …(6) } to deeply equal { parentName: 'Jordan Reyes',
   * …(7) }".
   */
  it("sends the field names the API declares", async () => {
    const fetchMock = respondWith(201, { publicId: PUBLIC_ID });
    globalThis.fetch = fetchMock;

    await consultationsApi.submit(SUBMISSION);

    const [url, init] = firstCall(fetchMock);
    expect(url).toBe("http://api.test/consultation-requests");
    expect(init.method).toBe("POST");

    expect(JSON.parse(init.body as string)).toEqual({
      parentName: "Jordan Reyes",
      email: "jordan@example.com",
      phone: "410-555-0142",
      childFirstName: "Maya",
      childAgeMonths: 30,
      concerns: "She has about ten words but is not combining them.",
      preferredContactMethod: "Either",
      sourceIpHash: SUBMISSION.sourceIpHash,
    });
  });

  /**
   * NO PROVIDER HEADER, and no session lookup.
   *
   * Every other client here forwards `X-Provider-Id`. Sending one from a public form would
   * be an unauthenticated caller nominating whose records to write into — and the API
   * ignores it on this route for the same reason, so this is the near half of a control
   * held at both ends.
   *
   * Control: the headers object — that it holds Content-Type and nothing else. An absence,
   * so falsified by adding `"X-Provider-Id": "anything"` → red, "AssertionError: expected
   * [ 'content-type', 'x-provider-id' ] to not include 'x-provider-id'".
   */
  it("forwards no provider identity", async () => {
    const fetchMock = respondWith(201, { publicId: PUBLIC_ID });
    globalThis.fetch = fetchMock;

    await consultationsApi.submit(SUBMISSION);

    const [, init] = firstCall(fetchMock);
    expect(
      Object.keys(init.headers as Record<string, string>).map((h) =>
        h.toLowerCase(),
      ),
    ).not.toContain("x-provider-id");
  });

  /**
   * A parent's enquiry must never sit in a cache, at any layer.
   *
   * Control: the `cache: "no-store"` on the fetch.
   * Deleted → red, "AssertionError: expected undefined to be 'no-store'".
   */
  it("is never cached", async () => {
    const fetchMock = respondWith(201, { publicId: PUBLIC_ID });
    globalThis.fetch = fetchMock;

    await consultationsApi.submit(SUBMISSION);

    const [, init] = firstCall(fetchMock);
    expect(init.cache).toBe("no-store");
  });

  /**
   * EVERY FAILURE IS "NOT STORED", and the caller cannot mistake one for a success.
   *
   * 503 is the practice having no clinician to receive it; 400 means the BFF's validation
   * and the API's disagree, which is a defect rather than a parent's mistake; 500 is the
   * API falling over. All three are the same fact to the person waiting: their enquiry is
   * not recorded.
   *
   * THE FOURTH CASE IS THE ONE THAT MAKES THIS A TEST. With only the first three, deleting
   * the status check leaves it GREEN — a body of `{detail: "nope"}` has no `publicId`, so
   * the missing-id guard below answers instead and the two clauses cover for each other
   * (the D066 F4 / D077 shape, found by running the deletion rather than by reading it).
   * A refusal that nevertheless carries a well-formed id isolates the status check: the
   * status is then the only thing that can refuse it.
   *
   * Control: the `if (!response.ok)` guard in consultationsApi.submit.
   * Deleted → red on the fourth case only, "expected { stored: true, publicId:
   * '3f7c9f2e-…' } to deeply equal { stored: false }". The first three stay green,
   * correctly: their real control is the missing-id guard.
   */
  it.each([
    ["the practice has no provider to receive it", 503, { detail: "nope" }],
    ["the API rejects what the BFF accepted", 400, { detail: "nope" }],
    ["the API fails outright", 500, { detail: "nope" }],
    ["a refusal still carries a well-formed id", 503, { publicId: PUBLIC_ID }],
  ])("reports not-stored when %s", async (_case, status, body) => {
    globalThis.fetch = respondWith(status, body);

    expect(await consultationsApi.submit(SUBMISSION)).toEqual({
      stored: false,
    });
  });

  /**
   * Control: the try/catch around the fetch in consultationsApi.submit.
   * Deleted → red, the call rejects: "TypeError: fetch failed".
   */
  it("reports not-stored when the API cannot be reached at all", async () => {
    globalThis.fetch = vi.fn(async () => {
      throw new TypeError("fetch failed");
    }) as unknown as typeof fetch;

    expect(await consultationsApi.submit(SUBMISSION)).toEqual({
      stored: false,
    });
  });

  /**
   * A 201 whose body is not what was promised is not a success either.
   *
   * Returning `stored: true` with an undefined id would confirm to a parent that their
   * enquiry was recorded on the strength of a status code alone.
   *
   * Control: the `if (!body?.publicId)` guard in consultationsApi.submit.
   * Deleted → red, "AssertionError: expected { stored: true, publicId: undefined } to
   * deeply equal { stored: false }" — which is the shape of the lie: a confirmation to a
   * parent naming no row.
   */
  it("reports not-stored when the response carries no id", async () => {
    globalThis.fetch = respondWith(201, { somethingElse: true });

    expect(await consultationsApi.submit(SUBMISSION)).toEqual({
      stored: false,
    });
  });

  /**
   * The diagnostic carries a STATUS CODE and nothing else.
   *
   * A failure that leaves no trace anywhere is how an unreachable API looks exactly like a
   * quiet week (docs/SECURITY.md). A failure that logs the request body is a child's name
   * and a description of their difficulties in a container log, which is non-negotiable #3.
   *
   * Control: the `report(String(response.status))` call on the non-OK path.
   * Deleted → red, "AssertionError: expected '' to contain '503'" — the failure leaves
   * nothing behind at all, which is how an unreachable API looks like a quiet week.
   */
  it("logs the status and none of the submission", async () => {
    const logged = vi.spyOn(console, "error").mockImplementation(() => {});
    globalThis.fetch = respondWith(503, { detail: "nope" });

    await consultationsApi.submit(SUBMISSION);

    const written = logged.mock.calls.flat().join(" ");
    expect(written).toContain("503");
    for (const secret of [
      SUBMISSION.parentName,
      SUBMISSION.childFirstName,
      SUBMISSION.concerns,
      SUBMISSION.email,
      SUBMISSION.phone,
    ]) {
      expect(written).not.toContain(secret);
    }
  });

  /**
   * Missing configuration fails loudly rather than quietly reporting not-stored.
   *
   * The two are very different: not-stored tells a parent to call, and is correct when the
   * API is down. A deployment with no API_BASE_URL is broken for every submission forever,
   * and dressing that up as a transient failure means nobody finds out.
   *
   * Control: the `if (!url) throw` in apiBaseUrl.
   * Deleted → red, "AssertionError: expected [Function] to throw error matching
   * /API_BASE_URL/ but got 'Cannot read properties of undefined (…'" — a TypeError from
   * somewhere further in, rather than a sentence naming what is unset.
   */
  it("throws when the API base URL is not configured", async () => {
    delete process.env.API_BASE_URL;
    globalThis.fetch = respondWith(201, { publicId: PUBLIC_ID });

    await expect(consultationsApi.submit(SUBMISSION)).rejects.toThrow(
      /API_BASE_URL/,
    );
  });
});
